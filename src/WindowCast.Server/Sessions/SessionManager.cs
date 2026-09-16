using System.Collections.Concurrent;
using WindowCast.Server.Capture;
using WindowCast.Server.Capture.Native;
using WindowCast.Server.Discovery;

namespace WindowCast.Server.Sessions;

public sealed class SessionException : Exception
{
    public int Status { get; }
    public SessionException(int status, string message) : base(message) => Status = status;
    public static SessionException MaxSessions() => new(429, "Maximum of 6 concurrent sessions reached");
    public static SessionException WindowNotFound() => new(404, "Window not found");
    public static SessionException DisplayNotFound() => new(404, "Display not found");
    public static SessionException NotFound() => new(404, "Session not found");
}

/// <summary>Owns all sessions, the shared GPU device, and the lifecycle poller. Mirrors the macOS SessionManager.</summary>
public sealed class SessionManager : IDisposable
{
    public const int MaxConcurrentSessions = 6;
    private const int MaxRestartsPerMinute = 3;

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _restartHistory = new();
    private readonly WindowDiscovery _windows;
    private readonly Lazy<GraphicsDevice> _device = new(GraphicsDevice.Create);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _lifecycle;

    public string? ActiveSessionId { get; private set; }
    public IReadOnlyCollection<Session> Sessions => (IReadOnlyCollection<Session>)_sessions.Values;
    public int Count => _sessions.Count;

    /// <summary>Raised for input JSON from any session's viewers.</summary>
    public event Action<Session, string>? InputReceived;

    public SessionManager(WindowDiscovery windows)
    {
        _windows = windows;
        _lifecycle = Task.Run(() => LifecycleLoop(_cts.Token));
    }

    public Session? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;
    public List<SessionInfo> Infos() => _sessions.Values.Select(s => s.Info).OrderBy(i => i.id, StringComparer.Ordinal).ToList();

    public Session CreateWindowSession(IntPtr hwnd)
    {
        if (_sessions.Count >= MaxConcurrentSessions) throw SessionException.MaxSessions();
        if (!Win32.IsWindow(hwnd)) throw SessionException.WindowNotFound();

        var pid = WindowDiscovery.OwningProcess(hwnd);
        var (path, _) = ProcessInfo.Describe(pid);
        var title = Win32.GetWindowText(hwnd);
        var capture = new WgcCapture(_device.Value, hwnd, CaptureTargetKind.Window);
        var session = new Session(NewId(), hwnd, false, title, AppKey.Encode(path), path, pid, capture);
        return Register(session);
    }

    public Session CreateDisplaySession(IntPtr hmon)
    {
        if (_sessions.Count >= MaxConcurrentSessions) throw SessionException.MaxSessions();
        var mon = Win32.EnumerateMonitors().FirstOrDefault(m => m.Handle == hmon);
        if (mon.Handle == IntPtr.Zero) throw SessionException.DisplayNotFound();
        var name = _windows.GetDisplays().First(d => d.displayID == hmon.ToInt64()).name;
        var capture = new WgcCapture(_device.Value, hmon, CaptureTargetKind.Monitor);
        var session = new Session(NewId(), hmon, true, name, "display", string.Empty, 0, capture);
        return Register(session);
    }

    public List<Session> CreateSessionsForApp(uint pid)
    {
        var eligible = _windows.EligibleWindowsForProcess(pid);
        if (eligible.Count == 0) throw SessionException.WindowNotFound();
        var created = new List<Session>();
        foreach (var hwnd in eligible)
        {
            if (_sessions.Count >= MaxConcurrentSessions) break;
            if (_sessions.Values.Any(s => s.Handle == hwnd)) continue;
            try { created.Add(CreateWindowSession(hwnd)); }
            catch (Exception ex) { Console.Error.WriteLine($"WindowCast: failed to create session for 0x{hwnd.ToInt64():X}: {ex.Message}"); }
        }
        return created;
    }

    /// <summary>Launches an app and streams its first eligible new window (polls for up to 15 s).</summary>
    public async Task<Session?> LaunchAndTrackAsync(string appIdOrPath)
    {
        var before = Win32.EnumerateAltTabWindows().ToHashSet();
        var expectedPath = AppDiscovery.ImagePathForAppId(appIdOrPath);
        // Match on the exe file name only: Windows 11 redirects the System32 notepad.exe to the Store Notepad
        // through an app execution alias, so the full path of the new process differs from what was launched.
        var expectedExe = expectedPath is null ? null : Path.GetFileName(expectedPath);
        AppDiscovery.Launch(appIdOrPath);

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            // After 6 s with no name match, accept any new eligible window (aliases, launchers, renamed hosts).
            var strict = expectedExe is not null && i < 12;
            foreach (var hwnd in Win32.EnumerateAltTabWindows())
            {
                if (before.Contains(hwnd) || Win32.IsIconic(hwnd)) continue;
                var b = Win32.GetFrameBounds(hwnd);
                if (b.Width <= 300 || b.Height <= 200) continue;
                if (strict)
                {
                    var (path, _) = ProcessInfo.Describe(WindowDiscovery.OwningProcess(hwnd));
                    if (!string.Equals(Path.GetFileName(path), expectedExe, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (_sessions.Values.Any(s => s.Handle == hwnd)) continue;
                return CreateWindowSession(hwnd);
            }
        }
        return null;
    }

    private Session Register(Session session)
    {
        session.InputReceived += (s, json) => InputReceived?.Invoke(s, json);
        session.Capture.Closed += () => OnTargetClosed(session);
        session.Capture.SizeChanged += size =>
        {
            if (size.Width > 50 && size.Height > 50)
                session.SendControl(new { type = "resize", width = size.Width, height = size.Height });
        };

        try
        {
            session.Capture.Start(captureCursor: true, showBorder: false);
        }
        catch (Exception ex)
        {
            session.Dispose();
            throw new SessionException(500, $"Capture failed to start: {ex.Message}");
        }

        session.Status = SessionStatus.connected;
        _sessions[session.Id] = session;
        if (ActiveSessionId is null) ActiveSessionId = session.Id;
        else session.IsPaused = true;
        Console.WriteLine($"WindowCast: Session {session.Id} created for '{session.Title}' ({(session.IsDisplay ? "display" : "window")} 0x{session.Handle.ToInt64():X})");
        return session;
    }

    private void OnTargetClosed(Session session)
    {
        if (!_sessions.ContainsKey(session.Id)) return;
        session.Status = SessionStatus.closed;
        var processAlive = !session.IsDisplay && _windows.WindowsForProcess(session.Pid).Count > 0;
        if (session.IsDisplay)
            session.SendControl(new { type = "error", reason = "display_removed" });
        else if (processAlive)
            session.SendControl(new { type = "error", reason = "window_closed" });
        else
            session.SendControl(new { type = "error", reason = "process_exited", appName = session.AppKey });
        Console.WriteLine($"WindowCast: Session {session.Id} target closed (process alive: {processAlive})");
    }

    public void Destroy(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return;
        _restartHistory.TryRemove(id, out _);
        session.Dispose();
        if (ActiveSessionId == id)
        {
            ActiveSessionId = _sessions.Keys.FirstOrDefault();
            if (ActiveSessionId is not null && _sessions.TryGetValue(ActiveSessionId, out var next)) next.IsPaused = false;
        }
        Console.WriteLine($"WindowCast: Session {id} destroyed");
    }

    public void SetActive(string id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return;
        if (ActiveSessionId is not null && ActiveSessionId != id && _sessions.TryGetValue(ActiveSessionId, out var old))
            old.IsPaused = true;
        session.IsPaused = false;
        ActiveSessionId = id;
    }

    /// <summary>Stops and restarts a session's capture, at most 3 times a minute.</summary>
    public bool RestartCapture(Session session)
    {
        if (!_sessions.ContainsKey(session.Id)) return false;
        var now = DateTime.UtcNow;
        var history = _restartHistory.GetOrAdd(session.Id, _ => new List<DateTime>());
        lock (history)
        {
            history.RemoveAll(t => (now - t).TotalSeconds >= 60);
            if (history.Count >= MaxRestartsPerMinute)
            {
                session.Status = SessionStatus.disconnected;
                Console.WriteLine($"WindowCast: Restart rate limit reached for session {session.Id}, giving up");
                return false;
            }
            history.Add(now);
        }
        try
        {
            session.Capture.Stop();
            session.Capture.Start(captureCursor: true, showBorder: false);
            session.Status = SessionStatus.connected;
            session.SendControl(new { type = "status", state = "restarted" });
            return true;
        }
        catch (Exception ex)
        {
            session.Status = SessionStatus.disconnected;
            Console.WriteLine($"WindowCast: Capture restart failed for session {session.Id}: {ex.Message}");
            return false;
        }
    }

    private async Task LifecycleLoop(CancellationToken ct)
    {
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch { break; }
            tick++;
            foreach (var session in _sessions.Values)
            {
                if (session.IsDisplay || session.Status == SessionStatus.closed) continue;
                // Title changes every 5 s (cheap: one GetWindowText per session).
                if (tick % 5 == 0)
                {
                    var title = Win32.GetWindowText(session.Handle);
                    if (title.Length > 0 && title != session.Title)
                    {
                        session.Title = title;
                        session.SendControl(new { type = "meta", title });
                    }
                }
            }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var id in _sessions.Keys.ToList()) Destroy(id);
        if (_device.IsValueCreated) _device.Value.Dispose();
    }
}
