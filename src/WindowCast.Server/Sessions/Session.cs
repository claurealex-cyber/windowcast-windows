using System.Text.Json;
using WindowCast.Server.Capture;
using WindowCast.Server.Capture.Encoding;
using WindowCast.Server.Capture.Native;

namespace WindowCast.Server.Sessions;

public enum SessionStatus { launching, connecting, connected, disconnected, closed }

public sealed record SessionInfo(string id, long windowID, string windowTitle, string appBundleID, int pid,
    string status, int width, int height, bool isDisplay, int viewers);

/// <summary>
/// One captured window or monitor plus everything attached to it: the capture, the JPEG encoder for
/// WebSocket viewers, and the viewers themselves. Mirrors the macOS Session.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly object _viewersLock = new();
    private readonly List<StreamViewer> _viewers = new();
    private readonly JpegEncoder _jpeg = new(15, 50);
    private readonly object _encodeLock = new();
    private int _disposed;

    public string Id { get; }
    public IntPtr Handle { get; }
    public bool IsDisplay { get; }
    public string Title { get; set; }
    public string AppKey { get; }
    public string AppImagePath { get; }
    public uint Pid { get; }
    public SessionStatus Status { get; set; } = SessionStatus.connecting;
    public WgcCapture Capture { get; }
    /// <summary>Only the active session feeds the (future) video track; JPEG for WebSocket viewers always runs.</summary>
    public bool IsPaused { get; set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public long FramesCaptured { get; private set; }
    public long FramesEncoded { get; private set; }
    public DateTime LastFrameAt { get; private set; }

    /// <summary>Input JSON from any viewer (handled by the input injector in M3).</summary>
    public event Action<Session, string>? InputReceived;

    public Session(string id, IntPtr handle, bool isDisplay, string title, string appKey, string appImagePath, uint pid, WgcCapture capture)
    {
        Id = id;
        Handle = handle;
        IsDisplay = isDisplay;
        Title = title;
        AppKey = appKey;
        AppImagePath = appImagePath;
        Pid = pid;
        Capture = capture;
        capture.FrameArrived += OnFrame;
    }

    public int ViewerCount { get { lock (_viewersLock) return _viewers.Count; } }

    public SessionInfo Info
    {
        get
        {
            var size = Capture.CurrentSize;
            return new SessionInfo(Id, Handle.ToInt64(), Title, AppKey, (int)Pid, Status.ToString(), size.Width, size.Height, IsDisplay, ViewerCount);
        }
    }

    public void AddViewer(StreamViewer viewer)
    {
        viewer.TextReceived += (_, json) => InputReceived?.Invoke(this, json);
        lock (_viewersLock) _viewers.Add(viewer);
        // A new viewer wants a picture right away even if the window is static.
        RequestRefresh();
    }

    public void RemoveViewer(StreamViewer viewer)
    {
        lock (_viewersLock) _viewers.Remove(viewer);
    }

    /// <summary>Sends a control message (meta, resize, error, status) to every viewer as JSON text.</summary>
    public void SendControl(object message)
    {
        var json = JsonSerializer.Serialize(message);
        lock (_viewersLock)
            foreach (var v in _viewers) v.EnqueueText(json);
    }

    private byte[]? _lastJpeg;

    /// <summary>Re-sends the last encoded frame; WGC only produces frames when content changes.</summary>
    public void RequestRefresh()
    {
        var last = _lastJpeg;
        if (last is null) return;
        lock (_viewersLock)
            foreach (var v in _viewers) v.EnqueueFrame(last);
    }

    private void OnFrame(CapturedFrame frame)
    {
        FramesCaptured++;
        LastFrameAt = DateTime.UtcNow;
        List<StreamViewer> viewers;
        lock (_viewersLock) viewers = _viewers.Count == 0 ? new List<StreamViewer>() : new List<StreamViewer>(_viewers);
        if (viewers.Count == 0)
        {
            // Nobody watching: keep a cheap snapshot ready for the next viewer, at most once a second.
            if ((DateTime.UtcNow - _lastIdleSnapshot).TotalSeconds < 1) return;
            _lastIdleSnapshot = DateTime.UtcNow;
        }

        lock (_encodeLock)
        {
            _jpeg.Encode(frame, chunk =>
            {
                FramesEncoded++;
                _lastJpeg = chunk.Data;
                foreach (var v in viewers) v.EnqueueFrame(chunk.Data);
            });
        }
    }

    private DateTime _lastIdleSnapshot = DateTime.MinValue;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Capture.FrameArrived -= OnFrame;
        Capture.Dispose();
        lock (_encodeLock) _jpeg.Dispose();
    }
}
