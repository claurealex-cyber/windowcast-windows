using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WindowCast.Server.Capture.Native;
using WindowCast.Server.Input;
using Xunit.Abstractions;

namespace WindowCast.Tests;

/// <summary>
/// M3 stress gate: injected mouse/keyboard land where and how the browser asked, measured through the
/// test pattern's own input log. These tests move the real cursor and type into a real window.
/// </summary>
[Collection("ports")]
public class InputStressTests
{
    private readonly ITestOutputHelper _out;
    public InputStressTests(ITestOutputHelper output) => _out = output;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    private static bool KeyHeld(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private sealed class Rig : IAsyncDisposable
    {
        public TestServer Server = null!;
        public Process Pattern = null!;
        public string LogPath = null!;
        public string SessionId = null!;
        public ClientWebSocket Ws = null!;
        public RECT Frame;
        public int OriginX, OriginY, ClientW, ClientH;

        public static async Task<Rig> Create(string title, string size = "800x500", string pos = "300,200")
        {
            var r = new Rig { Server = await TestServer.StartAsync() };
            r.LogPath = Path.Combine(Path.GetTempPath(), $"wc-input-{Guid.NewGuid():n}.log");
            r.Pattern = Process.Start(new ProcessStartInfo(SessionStressTests.TestPatternExe(),
                $"--size {size} --pos {pos} --title \"{title}\" --log-input \"{r.LogPath}\"") { UseShellExecute = false })!;
            await r.WaitForLog(l => l.Contains(" origin "), 5000);
            var origin = r.Lines().First(l => l.Contains(" origin ")).Split(' ');
            r.OriginX = int.Parse(origin[2]); r.OriginY = int.Parse(origin[3]); r.ClientW = int.Parse(origin[5]); r.ClientH = int.Parse(origin[6]);

            var windows = JsonDocument.Parse(await (await r.Server.GetAsync("/api/windows", r.Server.Host.Tokens.Current)).Content.ReadAsStringAsync()).RootElement;
            long hwnd = 0;
            foreach (var g in windows.EnumerateArray())
                foreach (var w in g.GetProperty("windows").EnumerateArray())
                    if (w.GetProperty("title").GetString()!.Contains(title)) hwnd = w.GetProperty("windowID").GetInt64();
            Assert.NotEqual(0, hwnd);
            r.Frame = Win32.GetFrameBounds(new IntPtr(hwnd));

            var req = r.Server.Request(HttpMethod.Post, "/api/sessions/create", r.Server.Host.Tokens.Current);
            req.Content = new StringContent(JsonSerializer.Serialize(new { windowID = hwnd }), Encoding.UTF8, "application/json");
            var resp = await r.Server.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            r.SessionId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

            r.Ws = new ClientWebSocket();
            await r.Ws.ConnectAsync(new Uri($"ws://127.0.0.1:{r.Server.Host.Port}/ws/stream/{r.SessionId}?token={r.Server.Host.Tokens.Current}"), CancellationToken.None);
            // The client sends "activate" when a tab becomes active; do the same so the window is in front.
            await r.Send(new { type = "activate" });
            await r.WaitForLog(l => l.Contains("activated"), 2000);
            await Task.Delay(150);
            return r;
        }

        public Task Send(object msg) => Ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg)), WebSocketMessageType.Text, true, CancellationToken.None);

        /// <summary>Normalized point for a client-area pixel of the pattern window.</summary>
        public (double x, double y) Norm(int cx, int cy)
        {
            var sx = OriginX + cx; var sy = OriginY + cy;
            return ((sx - Frame.Left + 0.5) / Frame.Width, (sy - Frame.Top + 0.5) / Frame.Height);
        }

        public async Task Click(int cx, int cy, int button = 0, bool ctrl = false)
        {
            var (x, y) = Norm(cx, cy);
            await Send(new { type = "mousedown", x, y, button, clickCount = 1, ctrlKey = ctrl });
            await Send(new { type = "mouseup", x, y, button, ctrlKey = ctrl });
        }

        public List<string> Lines()
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = new List<string>();
            while (sr.ReadLine() is { } l) lines.Add(l);
            return lines;
        }

        public async Task<bool> WaitForLog(Func<string, bool> pred, int ms)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(LogPath) && Lines().Any(pred)) return true;
                await Task.Delay(50);
            }
            return false;
        }

        public async Task<int> WaitForCount(Func<string, bool> pred, int expected, int ms)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            var count = 0;
            while (DateTime.UtcNow < deadline)
            {
                count = Lines().Count(pred);
                if (count >= expected) return count;
                await Task.Delay(50);
            }
            return count;
        }

        public async ValueTask DisposeAsync()
        {
            try { await Send(new { type = "deactivate" }); } catch { }
            try { Ws.Dispose(); } catch { }
            await Server.DisposeAsync();
            try { if (!Pattern.HasExited) Pattern.Kill(); Pattern.WaitForExit(3000); } catch { }
            try { File.Delete(LogPath); } catch { }
        }
    }

    private static (int x, int y) ParseDown(string line)
    {
        var p = line.Split(' '); // t down Left cx cy screen sx sy clicks n
        return (int.Parse(p[3]), int.Parse(p[4]));
    }

    [Fact]
    public void Mapper_hits_first_and_last_pixel()
    {
        var b = new RECT { Left = 100, Top = 50, Right = 900, Bottom = 550 };
        Assert.Equal((100, 50), CoordinateMapper.ToScreen(0, 0, b));
        Assert.Equal((899, 549), CoordinateMapper.ToScreen(1, 1, b));
        Assert.Equal((500, 300), CoordinateMapper.ToScreen(0.5, 0.5, b));
        Assert.True(KeyCodeMap.Count > 100);
        Assert.True(KeyCodeMap.Lookup("ArrowLeft")!.Value.Extended);
        Assert.Equal(0x41, KeyCodeMap.Lookup("KeyA")!.Value.VirtualKey);
        Assert.Null(KeyCodeMap.Lookup("Nope"));
    }

    [Fact]
    public async Task Corner_and_center_clicks_land_within_2px()
    {
        await using var r = await Rig.Create("WC Input Corners");
        var targets = new[] { (10, 10), (r.ClientW - 10, 10), (10, r.ClientH - 10), (r.ClientW - 10, r.ClientH - 10), (r.ClientW / 2, r.ClientH / 2) };
        var worst = 0;
        for (var i = 0; i < targets.Length; i++)
        {
            var (tx, ty) = targets[i];
            await r.Click(tx, ty);
            var arrived = await r.WaitForCount(l => l.Contains(" down Left "), i + 1, 3000) >= i + 1;
            if (!arrived) { _out.WriteLine($"click {i} at client ({tx},{ty}) -> norm {r.Norm(tx, ty)} frame {r.Frame} origin ({r.OriginX},{r.OriginY})"); foreach (var l in r.Lines()) _out.WriteLine("  log: " + l); }
            Assert.True(arrived, $"click {i} never arrived");
            var (cx, cy) = ParseDown(r.Lines().Where(l => l.Contains(" down Left ")).ElementAt(i));
            var err = Math.Max(Math.Abs(cx - tx), Math.Abs(cy - ty));
            worst = Math.Max(worst, err);
            _out.WriteLine($"target ({tx},{ty}) landed ({cx},{cy}) err={err}");
        }
        Assert.True(worst <= 2, $"worst error {worst} px");
    }

    [Fact]
    public async Task Fifty_clicks_all_hit_a_12px_target()
    {
        await using var r = await Rig.Create("WC Input Fifty");
        var (tx, ty) = (r.ClientW / 2, r.ClientH / 2);
        for (var i = 0; i < 50; i++) await r.Click(tx, ty);
        var count = await r.WaitForCount(l => l.Contains(" down Left "), 50, 8000);
        Assert.Equal(50, count);
        var misses = r.Lines().Where(l => l.Contains(" down Left ")).Select(ParseDown).Count(p => Math.Abs(p.x - tx) > 6 || Math.Abs(p.y - ty) > 6);
        Assert.Equal(0, misses);
        var ups = r.Lines().Count(l => l.Contains(" up Left "));
        Assert.Equal(50, ups);
    }

    [Fact]
    public async Task Text_path_types_500_characters_exactly()
    {
        await using var r = await Rig.Create("WC Input Text");
        await r.Click(r.ClientW / 2, r.ClientH / 2); // focus
        var sb = new StringBuilder();
        var pieces = new[] { "The quick brown fox ", "jumps over the lazy dog. ", "Straße, café, naïve, ", "señor, 日本語, ", "emoji 🎉🚀 ", "1234567890 !@#$%^&*() " };
        var k = 0;
        while (sb.Length < 500) sb.Append(pieces[k++ % pieces.Length]);
        var text = sb.ToString();
        foreach (var chunk in text.Chunk(40)) await r.Send(new { type = "text", chars = new string(chunk) });

        var count = await r.WaitForCount(l => l.Contains(" char "), text.Length, 15000);
        var received = new string(r.Lines().Where(l => l.Contains(" char ")).Select(l => (char)int.Parse(l.Split(' ')[2])).ToArray());
        _out.WriteLine($"sent {text.Length} units, received {received.Length}");
        Assert.Equal(text, received);
    }

    [Fact]
    public async Task Key_repeat_ctrl_combo_and_no_stuck_modifiers()
    {
        await using var r = await Rig.Create("WC Input Keys");
        await r.Click(r.ClientW / 2, r.ClientH / 2);

        // A letter key (WinForms eats arrows as dialog keys), spaced like browser autorepeat (~30 Hz):
        // Windows folds same-key key-downs that pile up in the queue into one message with a repeat count.
        for (var i = 0; i < 15; i++) { await r.Send(new { type = "keydown", key = "a", code = "KeyA", repeat = i > 0 }); await Task.Delay(40); }
        await r.Send(new { type = "keyup", key = "a", code = "KeyA" });
        var repeats = await r.WaitForCount(l => l.Contains("keydown A "), 15, 5000);
        if (repeats < 14) foreach (var l in r.Lines()) _out.WriteLine("  log: " + l);
        Assert.InRange(repeats, 14, 15);

        await r.Send(new { type = "keydown", key = "c", code = "KeyC", ctrlKey = true, repeat = false });
        await r.Send(new { type = "keyup", key = "c", code = "KeyC" });
        Assert.True(await r.WaitForLog(l => l.Contains("keydown C ctrl=True"), 3000), "Ctrl+C not seen with Control held");
        await Task.Delay(100);
        Assert.False(KeyHeld(0x11), "Control stuck after synthesized Ctrl+C");

        // Physical Shift held, then a tab switch (deactivate) must release it.
        await r.Send(new { type = "keydown", key = "Shift", code = "ShiftLeft", shiftKey = true, repeat = false });
        Assert.True(await r.WaitForLog(l => l.Contains("keydown ShiftKey"), 3000));
        await Task.Delay(50);
        Assert.True(KeyHeld(0x10), "Shift should be down");
        await r.Send(new { type = "deactivate" });
        await Task.Delay(150);
        Assert.False(KeyHeld(0x10), "Shift stuck after deactivate");

        // Wheel: JS deltaY +3 lines => three notches down (negative delta).
        var (x, y) = r.Norm(r.ClientW / 2, r.ClientH / 2);
        await r.Send(new { type = "wheel", x, y, deltaX = 0, deltaY = 3 });
        Assert.True(await r.WaitForLog(l => l.Contains(" wheel -360 "), 3000), "wheel down not seen");
        await r.Send(new { type = "wheel", x, y, deltaX = 0, deltaY = -1 });
        Assert.True(await r.WaitForLog(l => l.Contains(" wheel 120 "), 3000), "wheel up not seen");

        // Right click via the client's button=1 convention.
        await r.Click(r.ClientW / 2, r.ClientH / 2, button: 1);
        Assert.True(await r.WaitForLog(l => l.Contains(" down Right "), 3000), "right click not seen");
    }

    [Fact]
    public async Task Window_control_resize_maximize_half_restore()
    {
        await using var r = await Rig.Create("WC Input Control");
        var tok = r.Server.Host.Tokens.Current;
        async Task<HttpResponseMessage> Post(string path, object? body = null)
        {
            var req = r.Server.Request(HttpMethod.Post, path, tok);
            if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return await r.Server.Client.SendAsync(req);
        }
        async Task<(int w, int h)> CaptureSize()
        {
            await Task.Delay(400);
            var list = JsonDocument.Parse(await (await r.Server.GetAsync("/api/sessions", tok)).Content.ReadAsStringAsync()).RootElement;
            var s = list.EnumerateArray().Single();
            return (s.GetProperty("width").GetInt32(), s.GetProperty("height").GetInt32());
        }

        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/sessions/{r.SessionId}/resize", new { width = 640, height = 400 })).StatusCode);
        var (w1, h1) = await CaptureSize();
        _out.WriteLine($"after resize: capture {w1}x{h1}");
        Assert.InRange(w1, 638, 642); Assert.InRange(h1, 398, 402);

        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/sessions/{r.SessionId}/maximize")).StatusCode);
        Assert.True(await r.WaitForLog(l => l.Contains("state Maximized"), 3000), "not maximized");

        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/sessions/{r.SessionId}/half", new { left = true })).StatusCode);
        var mon = Win32.EnumerateMonitors().First(m => m.Primary);
        var (w2, h2) = await CaptureSize();
        _out.WriteLine($"after half-left: capture {w2}x{h2}, work area {mon.Work.Width}x{mon.Work.Height}");
        Assert.InRange(w2, mon.Work.Width / 2 - 2, mon.Work.Width / 2 + 2);
        Assert.InRange(h2, mon.Work.Height - 2, mon.Work.Height + 2);

        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/sessions/{r.SessionId}/maximize")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/sessions/{r.SessionId}/restore")).StatusCode);
        Assert.True(await r.WaitForLog(l => l.Contains("state Normal") && r.Lines().Any(m => m.Contains("state Maximized")), 3000), "not restored");
    }

    [Fact]
    public async Task Click_raises_a_window_covered_by_another()
    {
        await using var r = await Rig.Create("WC Input Behind", "600x400", "300,200");
        // Cover the target with a second window started later (on top).
        var cover = Process.Start(new ProcessStartInfo(SessionStressTests.TestPatternExe(), "--size 600x400 --pos 350,250 --title \"WC Input Cover\"") { UseShellExecute = false })!;
        try
        {
            await Task.Delay(1500);
            var before = r.Lines().Count(l => l.Contains(" down Left "));
            await r.Click(r.ClientW / 2, r.ClientH / 2);
            Assert.True(await r.WaitForCount(l => l.Contains(" down Left "), before + 1, 3000) >= before + 1, "click did not reach the covered window");
        }
        finally
        {
            try { cover.Kill(); } catch { }
        }
    }
}
