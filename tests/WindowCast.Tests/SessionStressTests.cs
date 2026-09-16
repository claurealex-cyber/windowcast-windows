using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit.Abstractions;

namespace WindowCast.Tests;

/// <summary>
/// M2 stress gate: discovery, sessions and WebSocket JPEG streaming against a real animated window
/// (tools/TestPattern). Runs on the desktop session of the machine executing the tests.
/// </summary>
[Collection("ports")]
public class SessionStressTests
{
    private readonly ITestOutputHelper _out;
    public SessionStressTests(ITestOutputHelper output) => _out = output;

    private static string TestPatternExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "TestPattern", "bin", "Debug", "net8.0-windows", "TestPattern.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Build tools/TestPattern first");
    }

    private static Process StartPattern(string title, string size = "800x500", string pos = "150,150", string extra = "")
    {
        var p = Process.Start(new ProcessStartInfo(TestPatternExe(), $"--size {size} --pos {pos} --title \"{title}\" {extra}") { UseShellExecute = false });
        Thread.Sleep(1500);
        return p!;
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(); p.WaitForExit(3000); } catch { }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> FindWindow(TestServer s, string title)
    {
        var windows = await Json(await s.GetAsync("/api/windows", s.Host.Tokens.Current));
        foreach (var g in windows.EnumerateArray())
            foreach (var w in g.GetProperty("windows").EnumerateArray())
                if (w.GetProperty("title").GetString()!.Contains(title)) return w;
        throw new Exception($"window '{title}' not listed");
    }

    private static async Task<JsonElement> CreateSession(TestServer s, long windowId)
    {
        var req = s.Request(HttpMethod.Post, "/api/sessions/create", s.Host.Tokens.Current);
        req.Content = new StringContent(JsonSerializer.Serialize(new { windowID = windowId }), System.Text.Encoding.UTF8, "application/json");
        var r = await s.Client.SendAsync(req);
        var body = await Json(r);
        if (r.StatusCode != HttpStatusCode.OK) throw new Exception($"create failed {(int)r.StatusCode}: {body}");
        return body;
    }

    [Fact]
    public async Task Enumerating_windows_is_fast_and_lists_the_pattern()
    {
        await using var s = await TestServer.StartAsync();
        var p = StartPattern("WC Enum Pattern");
        try
        {
            var sw = Stopwatch.StartNew();
            var r = await s.GetAsync("/api/windows", s.Host.Tokens.Current);
            sw.Stop();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var groups = await Json(r);
            var all = groups.EnumerateArray().SelectMany(g => g.GetProperty("windows").EnumerateArray()).ToList();
            _out.WriteLine($"{groups.GetArrayLength()} apps, {all.Count} windows in {sw.ElapsedMilliseconds} ms");
            Assert.True(sw.ElapsedMilliseconds < 200, $"enumeration took {sw.ElapsedMilliseconds} ms");
            Assert.Contains(all, w => w.GetProperty("title").GetString()!.Contains("WC Enum Pattern"));
            // No duplicates
            Assert.Equal(all.Count, all.Select(w => w.GetProperty("windowID").GetInt64()).Distinct().Count());

            var displays = await Json(await s.GetAsync("/api/displays", s.Host.Tokens.Current));
            Assert.True(displays.GetArrayLength() >= 1);
        }
        finally { Kill(p); }
    }

    [Fact]
    public async Task Websocket_viewer_receives_jpeg_frames()
    {
        await using var s = await TestServer.StartAsync();
        var p = StartPattern("WC Stream Pattern");
        try
        {
            var w = await FindWindow(s, "WC Stream Pattern");
            var info = await CreateSession(s, w.GetProperty("windowID").GetInt64());
            var id = info.GetProperty("id").GetString()!;
            Assert.Equal("connected", info.GetProperty("status").GetString());

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{s.Host.Port}/ws/stream/{id}?token={s.Host.Tokens.Current}"), CancellationToken.None);
            var buffer = new byte[2 * 1024 * 1024];
            var frames = 0; var bytes = 0L;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                using var cts = new CancellationTokenSource(2000);
                var res = await ws.ReceiveAsync(buffer, cts.Token);
                if (res.MessageType != WebSocketMessageType.Binary) continue;
                Assert.True(res.EndOfMessage);
                Assert.Equal(0xFF, buffer[0]); Assert.Equal(0xD8, buffer[1]); // JPEG SOI
                frames++; bytes += res.Count;
            }
            _out.WriteLine($"{frames} JPEG frames, {bytes / 1024} KB in 3 s");
            Assert.True(frames >= 24, $"only {frames} frames in 3 s");

            var list = await Json(await s.GetAsync("/api/sessions", s.Host.Tokens.Current));
            Assert.Equal(1, list.EnumerateArray().Single().GetProperty("viewers").GetInt32());
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        finally { Kill(p); }
    }

    [Fact]
    public async Task Wrong_token_on_websocket_is_refused()
    {
        await using var s = await TestServer.StartAsync();
        using var ws = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() =>
            ws.ConnectAsync(new Uri($"ws://127.0.0.1:{s.Host.Port}/ws/stream/nope?token=bad"), CancellationToken.None));
    }

    [Fact]
    public async Task Seventh_session_returns_429()
    {
        await using var s = await TestServer.StartAsync();
        var procs = Enumerable.Range(0, 7).Select(i => StartPattern($"WC Cap Pattern {i}", "400x300", $"{20 + i * 60},{20 + i * 40}")).ToList();
        try
        {
            var ids = new List<string>();
            for (var i = 0; i < 6; i++)
            {
                var w = await FindWindow(s, $"WC Cap Pattern {i}");
                ids.Add((await CreateSession(s, w.GetProperty("windowID").GetInt64())).GetProperty("id").GetString()!);
            }
            var w7 = await FindWindow(s, "WC Cap Pattern 6");
            var req = s.Request(HttpMethod.Post, "/api/sessions/create", s.Host.Tokens.Current);
            req.Content = new StringContent(JsonSerializer.Serialize(new { windowID = w7.GetProperty("windowID").GetInt64() }), System.Text.Encoding.UTF8, "application/json");
            var r = await s.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
            foreach (var id in ids) await s.GetAsync($"/api/sessions/{id}/delete", s.Host.Tokens.Current);
            Assert.Equal(0, (await Json(await s.GetAsync("/api/sessions", s.Host.Tokens.Current))).GetArrayLength());
        }
        finally { procs.ForEach(Kill); }
    }

    [Fact]
    public async Task Hundred_create_delete_cycles_do_not_leak()
    {
        await using var s = await TestServer.StartAsync();
        var p = StartPattern("WC Leak Pattern");
        try
        {
            var w = await FindWindow(s, "WC Leak Pattern");
            var hwnd = w.GetProperty("windowID").GetInt64();
            var proc = Process.GetCurrentProcess();

            // Warm up: the first ~20 cycles grow the thread pool, Kestrel and D3D/WGC caches, then it plateaus.
            for (var i = 0; i < 20; i++)
            {
                var info = await CreateSession(s, hwnd);
                await s.GetAsync($"/api/sessions/{info.GetProperty("id").GetString()}/delete", s.Host.Tokens.Current);
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            proc.Refresh();
            var handles0 = proc.HandleCount; var ws0 = proc.WorkingSet64;

            var withViewers = Environment.GetEnvironmentVariable("WC_NO_VIEWER") is null;
            for (var i = 0; i < 100; i++)
            {
                if (i % 20 == 0) { proc.Refresh(); _out.WriteLine($"cycle {i}: handles {proc.HandleCount} ws {proc.WorkingSet64 / 1048576} MB"); }
                var info = await CreateSession(s, hwnd);
                var id = info.GetProperty("id").GetString()!;
                if (withViewers && i % 10 == 0)
                {
                    // Attach a viewer on every tenth cycle to exercise the socket path too.
                    using var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{s.Host.Port}/ws/stream/{id}?token={s.Host.Tokens.Current}"), CancellationToken.None);
                    var buf = new byte[1024 * 1024];
                    using var cts = new CancellationTokenSource(2000);
                    try { await ws.ReceiveAsync(buf, cts.Token); } catch (OperationCanceledException) { }
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                var d = await s.GetAsync($"/api/sessions/{id}/delete", s.Host.Tokens.Current);
                Assert.Equal(HttpStatusCode.OK, d.StatusCode);
            }

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(500);
            proc.Refresh();
            var handles1 = proc.HandleCount; var ws1 = proc.WorkingSet64;
            _out.WriteLine($"handles {handles0} -> {handles1}, working set {ws0 / 1048576} -> {ws1 / 1048576} MB");
            Assert.True(handles1 - handles0 < 30, $"handle growth {handles1 - handles0} over 100 cycles");
            Assert.True(ws1 - ws0 < 40 * 1024 * 1024, $"working set grew {(ws1 - ws0) / 1048576} MB over 100 cycles");
            Assert.Equal(0, (await Json(await s.GetAsync("/api/sessions", s.Host.Tokens.Current))).GetArrayLength());
        }
        finally { Kill(p); }
    }

    [Fact]
    public async Task Closing_the_window_marks_session_closed_and_notifies_viewer()
    {
        await using var s = await TestServer.StartAsync();
        var p = StartPattern("WC Close Pattern", extra: "--close-after 4");
        var w = await FindWindow(s, "WC Close Pattern");
        var info = await CreateSession(s, w.GetProperty("windowID").GetInt64());
        var id = info.GetProperty("id").GetString()!;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{s.Host.Port}/ws/stream/{id}?token={s.Host.Tokens.Current}"), CancellationToken.None);
        var buffer = new byte[2 * 1024 * 1024];
        string? control = null;
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline && control is null)
        {
            using var cts = new CancellationTokenSource(3000);
            try
            {
                var res = await ws.ReceiveAsync(buffer, cts.Token);
                if (res.MessageType == WebSocketMessageType.Text)
                {
                    var text = System.Text.Encoding.UTF8.GetString(buffer, 0, res.Count);
                    if (text.Contains("\"error\"")) control = text;
                }
            }
            catch (OperationCanceledException) { }
        }
        _out.WriteLine($"control: {control}");
        Assert.NotNull(control);
        Assert.Contains("process_exited", control);
        var list = await Json(await s.GetAsync("/api/sessions", s.Host.Tokens.Current));
        Assert.Equal("closed", list.EnumerateArray().Single().GetProperty("status").GetString());
        Kill(p);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", "Notepad")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Calculator")]
    [InlineData("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", "Settings")]
    public async Task Launch_and_track_streams_the_new_window(string appId, string expectedTitle)
    {
        await using var s = await TestServer.StartAsync();
        var key = WindowCast.Server.Discovery.AppKey.Encode(appId);
        var req = s.Request(HttpMethod.Post, "/api/launch/track", s.Host.Tokens.Current);
        req.Content = new StringContent(JsonSerializer.Serialize(new { bundleID = key, appName = expectedTitle }), System.Text.Encoding.UTF8, "application/json");
        var sw = Stopwatch.StartNew();
        var r = await s.Client.SendAsync(req);
        var info = await Json(r);
        _out.WriteLine($"{expectedTitle}: {(int)r.StatusCode} in {sw.ElapsedMilliseconds} ms -> {info}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = info.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id), "no session created within 15 s");
        Assert.Equal("connected", info.GetProperty("status").GetString());
        Assert.Contains(expectedTitle, info.GetProperty("windowTitle").GetString(), StringComparison.OrdinalIgnoreCase);

        // Close what we launched.
        var pid = info.GetProperty("pid").GetInt32();
        await s.GetAsync($"/api/sessions/{id}/delete", s.Host.Tokens.Current);
        try { Process.GetProcessById(pid).Kill(); } catch { }
    }
}
