using System.Text.Json;
using WindowCast.Server.Capture.Native;
using WindowCast.Server.Input.Native;
using WindowCast.Server.Sessions;

namespace WindowCast.Server.Input;

/// <summary>
/// Turns the browser's input JSON (same protocol as the macOS client) into SendInput calls.
/// Coordinates are normalized over the streamed picture; modifier flags on an event are synthesized as
/// real modifier key presses when the physical modifier is not already held.
/// </summary>
public sealed class InputInjector
{
    private readonly object _lock = new();
    private readonly HashSet<ushort> _synthesizedModifiers = new();
    private IntPtr _lastFocused;
    public long EventsInjected { get; private set; }
    public long EventsDropped { get; private set; }

    public void Process(Session session, string json)
    {
        JsonElement msg;
        try { msg = JsonDocument.Parse(json).RootElement; }
        catch { return; }
        if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("type", out var t)) return;
        var type = t.GetString();

        lock (_lock)
        {
            switch (type)
            {
                case "mousemove": MouseMove(session, msg); break;
                case "mousedown": MouseButton(session, msg, down: true); break;
                case "mouseup": MouseButton(session, msg, down: false); break;
                case "wheel": Wheel(session, msg); break;
                case "keydown": KeyEvent(msg, down: true); break;
                case "keyup": KeyEvent(msg, down: false); break;
                case "text": Text(msg); break;
                case "clipboard": if (msg.TryGetProperty("text", out var ct)) ClipboardWriter.SetText(ct.GetString() ?? ""); break;
                case "activate": Focus(session); break;
                case "deactivate": ReleaseAllModifiers(); _lastFocused = IntPtr.Zero; break;
            }
        }
    }

    // ---- Mouse ----

    private RECT Bounds(Session session)
    {
        if (session.IsDisplay)
        {
            var mon = Win32.EnumerateMonitors().FirstOrDefault(m => m.Handle == session.Handle);
            return mon.Bounds;
        }
        return Win32.GetFrameBounds(session.Handle);
    }

    private bool TryPoint(Session session, JsonElement msg, out int x, out int y)
    {
        x = y = 0;
        if (!msg.TryGetProperty("x", out var px) || !msg.TryGetProperty("y", out var py)) return false;
        var bounds = Bounds(session);
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        (x, y) = CoordinateMapper.ToScreen(px.GetDouble(), py.GetDouble(), bounds);
        return true;
    }

    private void MouseMove(Session session, JsonElement msg)
    {
        if (!TryPoint(session, msg, out var x, out var y)) return;
        Send(MouseInput(x, y, InputNative.MOUSEEVENTF_MOVE));
    }

    private void MouseButton(Session session, JsonElement msg, bool down)
    {
        if (!TryPoint(session, msg, out var x, out var y)) return;
        // Client convention (shared with macOS client): 0 = left, 1 = right, 2 = middle.
        var button = msg.TryGetProperty("button", out var b) ? b.GetInt32() : 0;
        var flag = (button, down) switch
        {
            (1, true) => InputNative.MOUSEEVENTF_RIGHTDOWN,
            (1, false) => InputNative.MOUSEEVENTF_RIGHTUP,
            (2, true) => InputNative.MOUSEEVENTF_MIDDLEDOWN,
            (2, false) => InputNative.MOUSEEVENTF_MIDDLEUP,
            (_, true) => InputNative.MOUSEEVENTF_LEFTDOWN,
            (_, false) => InputNative.MOUSEEVENTF_LEFTUP,
        };

        if (down && !session.IsDisplay)
        {
            // Raise the window if something else covers the click point, otherwise the click lands elsewhere.
            var under = WindowControl.RootWindowAt(x, y);
            if (under != session.Handle || InputNative.GetForegroundWindow() != session.Handle)
            {
                Focus(session);
                // Give the window manager a moment to re-stack before the click lands.
                for (var i = 0; i < 5 && WindowControl.RootWindowAt(x, y) != session.Handle; i++) Thread.Sleep(20);
            }
        }

        var mods = down ? PressModifiersFor(msg, key: 0xFFFF) : Array.Empty<ushort>();
        Send(MouseInput(x, y, InputNative.MOUSEEVENTF_MOVE), MouseInput(x, y, flag));
        if (!down) ReleaseSynthesized(0xFFFF);
    }

    private void Wheel(Session session, JsonElement msg)
    {
        if (!TryPoint(session, msg, out var x, out var y)) return;
        var dy = msg.TryGetProperty("deltaY", out var pdy) ? pdy.GetDouble() : 0;
        var dx = msg.TryGetProperty("deltaX", out var pdx) ? pdx.GetDouble() : 0;
        var inputs = new List<INPUT> { MouseInput(x, y, InputNative.MOUSEEVENTF_MOVE) };
        if (dy != 0)
        {
            var w = MouseInput(x, y, InputNative.MOUSEEVENTF_WHEEL);
            w.u.mi.mouseData = unchecked((uint)(int)Math.Round(-dy * InputNative.WHEEL_DELTA)); // JS +y = down = negative wheel
            inputs.Add(w);
        }
        if (dx != 0)
        {
            var w = MouseInput(x, y, InputNative.MOUSEEVENTF_HWHEEL);
            w.u.mi.mouseData = unchecked((uint)(int)Math.Round(dx * InputNative.WHEEL_DELTA));
            inputs.Add(w);
        }
        var mods = PressModifiersFor(msg, key: 0xFFFE);
        Send(inputs.ToArray());
        ReleaseSynthesized(0xFFFE);
    }

    private static INPUT MouseInput(int x, int y, uint flags)
    {
        var (ax, ay) = InputNative.ToAbsolute(x, y, InputNative.GetVirtualScreen());
        return new INPUT
        {
            type = InputNative.INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = flags | InputNative.MOUSEEVENTF_ABSOLUTE | InputNative.MOUSEEVENTF_VIRTUALDESK } },
        };
    }

    // ---- Keyboard ----

    private void KeyEvent(JsonElement msg, bool down)
    {
        var code = msg.TryGetProperty("code", out var c) ? c.GetString() : null;
        var key = KeyCodeMap.Lookup(code);
        if (key is null)
        {
            // Unknown physical code: fall back to typing the printable "key" value.
            if (down && msg.TryGetProperty("key", out var k) && k.GetString() is { Length: 1 } ch) TypeText(ch);
            else EventsDropped++;
            return;
        }
        var vk = key.Value.VirtualKey;
        if (down)
        {
            if (!KeyCodeMap.IsModifier(vk)) PressModifiersFor(msg, vk);
            Send(KeyInput(key.Value, down: true));
        }
        else
        {
            Send(KeyInput(key.Value, down: false));
            if (!KeyCodeMap.IsModifier(vk)) ReleaseSynthesized(vk);
        }
    }

    private static INPUT KeyInput(KeyCodeMap.Key key, bool down)
    {
        var flags = down ? 0u : InputNative.KEYEVENTF_KEYUP;
        if (key.Extended) flags |= InputNative.KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = InputNative.INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = key.VirtualKey, wScan = KeyCodeMap.ScanCode(key.VirtualKey), dwFlags = flags } },
        };
    }

    private void Text(JsonElement msg)
    {
        if (!msg.TryGetProperty("chars", out var c)) return;
        var chars = c.GetString();
        if (!string.IsNullOrEmpty(chars)) TypeText(chars);
    }

    private void TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var unit in text)
        {
            inputs.Add(new INPUT { type = InputNative.INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = unit, dwFlags = InputNative.KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = InputNative.INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = unit, dwFlags = InputNative.KEYEVENTF_UNICODE | InputNative.KEYEVENTF_KEYUP } } });
        }
        // Batches of 32 chars keep each SendInput call small and ordered.
        for (var i = 0; i < inputs.Count; i += 64)
            Send(inputs.Skip(i).Take(64).ToArray());
    }

    // ---- Modifiers ----

    private readonly Dictionary<ushort, List<ushort>> _synthesizedFor = new();

    /// <summary>Press modifiers the message flags ask for that are not physically held; remember them per key.</summary>
    private ushort[] PressModifiersFor(JsonElement msg, ushort key)
    {
        var wanted = new List<ushort>();
        if (Flag(msg, "shiftKey") && !InputNative.IsKeyDown(InputNative.VK_SHIFT)) wanted.Add(InputNative.VK_LSHIFT);
        if (Flag(msg, "ctrlKey") && !InputNative.IsKeyDown(InputNative.VK_CONTROL)) wanted.Add(InputNative.VK_LCONTROL);
        if (Flag(msg, "altKey") && !InputNative.IsKeyDown(InputNative.VK_MENU)) wanted.Add(InputNative.VK_LMENU);
        if (Flag(msg, "metaKey") && !InputNative.IsKeyDown(InputNative.VK_LWIN)) wanted.Add(InputNative.VK_LWIN);
        if (wanted.Count == 0) return Array.Empty<ushort>();
        Send(wanted.Select(vk => KeyInput(new KeyCodeMap.Key(vk, vk is 0x5B or 0xA3 or 0xA5), true)).ToArray());
        foreach (var vk in wanted) _synthesizedModifiers.Add(vk);
        _synthesizedFor[key] = wanted;
        return wanted.ToArray();
    }

    private void ReleaseSynthesized(ushort key)
    {
        if (!_synthesizedFor.Remove(key, out var list) || list.Count == 0) return;
        Send(list.Select(vk => KeyInput(new KeyCodeMap.Key(vk, vk is 0x5B or 0xA3 or 0xA5), false)).ToArray());
        foreach (var vk in list) _synthesizedModifiers.Remove(vk);
    }

    /// <summary>Key-up for every modifier, synthesized or forwarded, so nothing stays stuck after a tab switch.</summary>
    public void ReleaseAllModifiers()
    {
        var mods = new ushort[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };
        Send(mods.Select(vk => KeyInput(new KeyCodeMap.Key(vk, vk is 0x5B or 0x5C or 0xA3 or 0xA5), false)).ToArray());
        _synthesizedModifiers.Clear();
        _synthesizedFor.Clear();
    }

    private static bool Flag(JsonElement msg, string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ---- Focus ----

    private void Focus(Session session)
    {
        if (session.IsDisplay) return;
        WindowControl.Focus(session.Handle);
        _lastFocused = session.Handle;
    }

    private void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var sent = InputNative.SendInput((uint)inputs.Length, inputs, INPUT.Size);
        EventsInjected += sent;
        if (sent != inputs.Length) EventsDropped += inputs.Length - sent;
    }
}
