using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Vortice.Mathematics;
using WindowCast.Server.Capture.Native;

namespace WindowCast.Server.Capture;

public enum CaptureTargetKind { Window, Monitor }

/// <summary>
/// Windows.Graphics.Capture session for one window or monitor. Frames are copied to a CPU BGRA buffer
/// and handed to <see cref="FrameArrived"/> on a worker thread. Handles content-size changes by
/// recreating the frame pool, and raises <see cref="Closed"/> when the target goes away.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    private const int PoolFrames = 2;

    private readonly GraphicsDevice _device;
    private readonly GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private byte[] _buffer = Array.Empty<byte>();
    private long _sequence;
    private int _disposed;
    private int _inCallback;

    public IntPtr TargetHandle { get; }
    public CaptureTargetKind Kind { get; }
    public SizeInt32 CurrentSize => _poolSize;
    public long FramesDelivered => Interlocked.Read(ref _sequence);
    public long FramesDropped => Interlocked.Read(ref _dropped);
    private long _dropped;
    public bool BorderDisabled { get; private set; }
    public bool IsRunning => _session is not null;

    /// <summary>Called on a worker thread with a reusable buffer. Do not hold the frame after returning.</summary>
    public event Action<CapturedFrame>? FrameArrived;
    public event Action<SizeInt32>? SizeChanged;
    public event Action? Closed;

    public WgcCapture(GraphicsDevice device, IntPtr handle, CaptureTargetKind kind)
    {
        _device = device;
        TargetHandle = handle;
        Kind = kind;
        _item = kind == CaptureTargetKind.Window
            ? CaptureInterop.CreateItemForWindow(handle)
            : CaptureInterop.CreateItemForMonitor(handle);
        // Keep the delegate so it can be unsubscribed: the native item holds a CCW to this handler, the
        // handler holds this object, this object holds the item's RCW -> a cycle the GC cannot see.
        _closedHandler = (_, _) => RaiseClosed();
        _item.Closed += _closedHandler;
    }

    private readonly Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object> _closedHandler;

    private int _closedRaised;
    private Timer? _watchdog;

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) != 0) return;
        Closed?.Invoke();
    }

    /// <summary>
    /// GraphicsCaptureItem.Closed needs a DispatcherQueue on the creating thread to be delivered, which a
    /// headless server does not have. Poll the HWND instead so a closed or killed window is noticed within 500 ms.
    /// </summary>
    private void StartWatchdog()
    {
        if (Kind != CaptureTargetKind.Window) return;
        _watchdog = new Timer(_ =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!Win32.IsWindow(TargetHandle)) RaiseClosed();
        }, null, 500, 500);
    }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public void Start(bool captureCursor = true, bool showBorder = false)
    {
        if (_session is not null) return;
        _poolSize = _item.Size;
        if (_poolSize.Width <= 0 || _poolSize.Height <= 0)
            throw new InvalidOperationException($"Capture target has no size ({_poolSize.Width}x{_poolSize.Height}); is it minimized?");

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device.WinRT, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolFrames, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);

        try { _session.IsCursorCaptureEnabled = captureCursor; } catch { }
        if (!showBorder)
        {
            // Needs Windows 11 21H2+. On older builds the setter throws; the yellow border then stays.
            try { _session.IsBorderRequired = false; BorderDisabled = true; } catch { BorderDisabled = false; }
        }

        _session.StartCapture();
        StartWatchdog();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // If the consumer is slow, drop rather than queue: the pool only holds PoolFrames anyway.
        if (Interlocked.CompareExchange(ref _inCallback, 1, 0) != 0)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null) return;

            var size = frame.ContentSize;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                if (size.Width <= 0 || size.Height <= 0) return;
                _poolSize = size;
                pool.Recreate(_device.WinRT, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolFrames, size);
                SizeChanged?.Invoke(size);
                // The frame we hold is at the old size; still deliver it, the next one will match.
            }

            var texPtr = CaptureInterop.GetTexturePointer(frame.Surface);
            using var texture = new ID3D11Texture2D(texPtr);
            CopyToCpu(texture, size.Width, size.Height, frame.SystemRelativeTime);
        }
        finally
        {
            Volatile.Write(ref _inCallback, 0);
        }
    }

    private void CopyToCpu(ID3D11Texture2D source, int width, int height, TimeSpan timestamp)
    {
        var stride = width * 4;
        var needed = stride * height;
        if (_buffer.Length < needed) _buffer = new byte[needed];

        lock (_device.ContextLock)
        {
            if (_staging is null || _staging.Description.Width != (uint)width || _staging.Description.Height != (uint)height)
            {
                _staging?.Dispose();
                var desc = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                };
                _staging = _device.D3D.CreateTexture2D(ref desc);
            }

            // The pool texture can be larger than ContentSize; copy only the content box.
            var box = new Box(0, 0, 0, width, height, 1);
            _device.Context.CopySubresourceRegion(_staging, 0, 0, 0, 0, source, 0, box);

            var mapped = _device.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    var src = (byte*)mapped.DataPointer;
                    fixed (byte* dst = _buffer)
                    {
                        if (mapped.RowPitch == stride)
                        {
                            Buffer.MemoryCopy(src, dst, _buffer.Length, needed);
                        }
                        else
                        {
                            for (var y = 0; y < height; y++)
                                Buffer.MemoryCopy(src + y * mapped.RowPitch, dst + y * stride, stride, stride);
                        }
                    }
                }
            }
            finally
            {
                _device.Context.Unmap(_staging, 0);
            }
        }

        var seq = Interlocked.Increment(ref _sequence);
        FrameArrived?.Invoke(new CapturedFrame(_buffer, width, height, stride, timestamp, seq));
    }

    public void Stop()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        _watchdog?.Dispose();
        _watchdog = null;
        try { session.Dispose(); } catch { }
        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrameArrived;
            try { _pool.Dispose(); } catch { }
            _pool = null;
        }
        // Wait for an in-flight callback to finish before releasing the staging texture.
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _inCallback) != 0 && sw.ElapsedMilliseconds < 500) Thread.Sleep(1);
        lock (_device.ContextLock)
        {
            _staging?.Dispose();
            _staging = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        try { _item.Closed -= _closedHandler; } catch { }

    }
}
