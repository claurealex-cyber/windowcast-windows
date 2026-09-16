using WindowCast.Server.Input.Native;

namespace WindowCast.Server.Input;

/// <summary>Maps JavaScript KeyboardEvent.code to a Windows virtual key, with the extended-key flag where needed.</summary>
public static class KeyCodeMap
{
    public readonly record struct Key(ushort VirtualKey, bool Extended);

    private static readonly Dictionary<string, Key> Map = Build();

    private static Dictionary<string, Key> Build()
    {
        var m = new Dictionary<string, Key>(StringComparer.Ordinal);
        for (var c = 'A'; c <= 'Z'; c++) m[$"Key{c}"] = new Key((ushort)c, false);
        for (var d = '0'; d <= '9'; d++) m[$"Digit{d}"] = new Key((ushort)d, false);
        for (var f = 1; f <= 24; f++) m[$"F{f}"] = new Key((ushort)(0x70 + f - 1), false);
        for (var n = 0; n <= 9; n++) m[$"Numpad{n}"] = new Key((ushort)(0x60 + n), false);

        void Add(string code, int vk, bool ext = false) => m[code] = new Key((ushort)vk, ext);
        Add("Space", 0x20); Add("Enter", 0x0D); Add("Tab", 0x09); Add("Escape", 0x1B); Add("Backspace", 0x08);
        Add("Delete", 0x2E, true); Add("Insert", 0x2D, true); Add("Home", 0x24, true); Add("End", 0x23, true);
        Add("PageUp", 0x21, true); Add("PageDown", 0x22, true);
        Add("ArrowUp", 0x26, true); Add("ArrowDown", 0x28, true); Add("ArrowLeft", 0x25, true); Add("ArrowRight", 0x27, true);
        Add("ShiftLeft", 0xA0); Add("ShiftRight", 0xA1); Add("ControlLeft", 0xA2); Add("ControlRight", 0xA3, true);
        Add("AltLeft", 0xA4); Add("AltRight", 0xA5, true); Add("MetaLeft", 0x5B, true); Add("MetaRight", 0x5C, true);
        Add("CapsLock", 0x14); Add("NumLock", 0x90, true); Add("ScrollLock", 0x91); Add("PrintScreen", 0x2C, true);
        Add("Pause", 0x13); Add("ContextMenu", 0x5D, true);
        Add("Minus", 0xBD); Add("Equal", 0xBB); Add("BracketLeft", 0xDB); Add("BracketRight", 0xDD); Add("Backslash", 0xDC);
        Add("Semicolon", 0xBA); Add("Quote", 0xDE); Add("Backquote", 0xC0); Add("Comma", 0xBC); Add("Period", 0xBE); Add("Slash", 0xBF);
        Add("IntlBackslash", 0xE2);
        Add("NumpadAdd", 0x6B); Add("NumpadSubtract", 0x6D); Add("NumpadMultiply", 0x6A); Add("NumpadDivide", 0x6F, true);
        Add("NumpadDecimal", 0x6E); Add("NumpadEnter", 0x0D, true);
        // The toolbar sends key names as codes for a few keys.
        m["Esc"] = m["Escape"]; m["Del"] = m["Delete"];
        return m;
    }

    public static Key? Lookup(string? code) => code is not null && Map.TryGetValue(code, out var k) ? k : null;

    public static bool IsModifier(ushort vk) => vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x10 or 0x11 or 0x12;

    /// <summary>Layout-aware scan code for a virtual key (0 when the layout has none).</summary>
    public static ushort ScanCode(ushort vk) => (ushort)InputNative.MapVirtualKeyW(vk, InputNative.MAPVK_VK_TO_VSC);

    public static int Count => Map.Count;
}
