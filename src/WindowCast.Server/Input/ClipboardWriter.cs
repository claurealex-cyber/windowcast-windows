using System.Runtime.InteropServices;

namespace WindowCast.Server.Input;

/// <summary>Sets the clipboard text from any thread (clipboard needs a real window/STA-ish owner; we retry briefly).</summary>
public static class ClipboardWriter
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr h);

    public static bool SetText(string text)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var bytes = (text.Length + 1) * 2;
                    var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (h == IntPtr.Zero) return false;
                    var p = GlobalLock(h);
                    Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                    Marshal.WriteInt16(p, text.Length * 2, 0);
                    GlobalUnlock(h);
                    if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return false; }
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(20);
        }
        return false;
    }
}
