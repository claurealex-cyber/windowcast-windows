using WindowCast.Server.Capture.Native;
using WindowCast.Server.Input.Native;

namespace WindowCast.Server.Input;

/// <summary>Move, resize, maximize, snap and focus for a session's window. Sizes are visible-frame sizes.</summary>
public static class WindowControl
{
    /// <summary>Difference between GetWindowRect (with invisible resize borders) and the visible DWM frame.</summary>
    private static (int dx, int dy, int dw, int dh) BorderDelta(IntPtr hwnd)
    {
        Win32.GetWindowRect(hwnd, out var wr);
        var fr = Win32.GetFrameBounds(hwnd);
        return (wr.Left - fr.Left, wr.Top - fr.Top, wr.Width - fr.Width, wr.Height - fr.Height);
    }

    public static bool Resize(IntPtr hwnd, int width, int height)
    {
        if (!Win32.IsWindow(hwnd)) return false;
        if (InputNative.IsZoomed(hwnd)) InputNative.ShowWindow(hwnd, InputNative.SW_RESTORE);
        var (_, _, dw, dh) = BorderDelta(hwnd);
        return InputNative.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width + dw, height + dh,
            InputNative.SWP_NOMOVE | InputNative.SWP_NOZORDER | InputNative.SWP_NOACTIVATE);
    }

    public static bool Move(IntPtr hwnd, int x, int y)
    {
        if (!Win32.IsWindow(hwnd)) return false;
        var (dx, dy, _, _) = BorderDelta(hwnd);
        return InputNative.SetWindowPos(hwnd, IntPtr.Zero, x + dx, y + dy, 0, 0,
            InputNative.SWP_NOSIZE | InputNative.SWP_NOZORDER | InputNative.SWP_NOACTIVATE);
    }

    public static bool Maximize(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd)) return false;
        return InputNative.ShowWindow(hwnd, InputNative.SW_MAXIMIZE);
    }

    public static bool Restore(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd)) return false;
        return InputNative.ShowWindow(hwnd, InputNative.SW_RESTORE);
    }

    /// <summary>Snap to the left or right half of the window's monitor work area.</summary>
    public static bool Half(IntPtr hwnd, bool left)
    {
        if (!Win32.IsWindow(hwnd)) return false;
        if (InputNative.IsZoomed(hwnd)) InputNative.ShowWindow(hwnd, InputNative.SW_RESTORE);
        var hmon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mon = Win32.EnumerateMonitors().FirstOrDefault(m => m.Handle == hmon);
        if (mon.Handle == IntPtr.Zero) return false;
        var work = mon.Work;
        var halfW = work.Width / 2;
        var x = left ? work.Left : work.Left + halfW;
        var (dx, dy, dw, dh) = BorderDelta(hwnd);
        return InputNative.SetWindowPos(hwnd, IntPtr.Zero, x + dx, work.Top + dy, halfW + dw, work.Height + dh,
            InputNative.SWP_NOZORDER | InputNative.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Bring the window to the foreground so injected input lands on it. Windows only lets the process that
    /// owns the current foreground input do this, hence the AttachThreadInput dance.
    /// </summary>
    public static bool Focus(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd)) return false;
        if (Win32.IsIconic(hwnd)) InputNative.ShowWindow(hwnd, InputNative.SW_RESTORE);
        if (InputNative.GetForegroundWindow() == hwnd) return true;

        var fg = InputNative.GetForegroundWindow();
        var fgThread = fg == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(fg, out _);
        var me = InputNative.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && InputNative.AttachThreadInput(me, fgThread, true);
        try
        {
            InputNative.AllowSetForegroundWindow(InputNative.ASFW_ANY);
            InputNative.BringWindowToTop(hwnd);
            InputNative.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) InputNative.AttachThreadInput(me, fgThread, false);
        }
        // Escalate: an Alt tap satisfies the "received last input" rule, then the shell's own switcher.
        for (var attempt = 0; attempt < 3 && InputNative.GetForegroundWindow() != hwnd; attempt++)
        {
            var tap = new[]
            {
                new INPUT { type = InputNative.INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = InputNative.VK_MENU } } },
                new INPUT { type = InputNative.INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = InputNative.VK_MENU, dwFlags = InputNative.KEYEVENTF_KEYUP } } },
            };
            InputNative.SendInput(2, tap, INPUT.Size);
            InputNative.SetForegroundWindow(hwnd);
            if (InputNative.GetForegroundWindow() == hwnd) break;
            InputNative.SwitchToThisWindow(hwnd, true);
            Thread.Sleep(30);
        }
        return InputNative.GetForegroundWindow() == hwnd;
    }

    /// <summary>Root window under a screen point (for "is something covering the target?").</summary>
    public static IntPtr RootWindowAt(int x, int y)
    {
        var h = InputNative.WindowFromPoint(new InputNative.POINT(x, y));
        return h == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestor(h, 2 /* GA_ROOT */);
    }
}
