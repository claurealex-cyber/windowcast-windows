using System.Runtime.InteropServices;
using WindowCast.Server.Capture.Native;

namespace WindowCast.Server.Discovery;

public sealed record WindowFrame(int x, int y, int width, int height);
public sealed record WindowInfo(long windowID, string title, WindowFrame frame, bool minimized);
public sealed record AppWindowGroup(string appName, string bundleID, int pid, string iconUrl, List<WindowInfo> windows);
public sealed record DisplayInfo(long displayID, int width, int height, string name, int x, int y, bool primary);

/// <summary>Alt-Tab windows grouped by owning process, plus monitors. Mirrors the macOS WindowDiscovery JSON.</summary>
public sealed class WindowDiscovery
{
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, Win32.EnumWindowsProc cb, IntPtr lParam);

    public List<AppWindowGroup> GetWindows()
    {
        var groups = new Dictionary<uint, (string name, string path, List<WindowInfo> windows)>();
        foreach (var hwnd in Win32.EnumerateAltTabWindows())
        {
            var pid = OwningProcess(hwnd);
            if (pid == 0) continue;
            var bounds = Win32.GetFrameBounds(hwnd);
            var info = new WindowInfo(hwnd.ToInt64(), Win32.GetWindowText(hwnd),
                new WindowFrame(bounds.Left, bounds.Top, bounds.Width, bounds.Height), Win32.IsIconic(hwnd));
            if (!groups.TryGetValue(pid, out var g))
            {
                var (path, name) = ProcessInfo.Describe(pid);
                g = (name, path, new List<WindowInfo>());
                groups[pid] = g;
            }
            g.windows.Add(info);
        }

        return groups
            .Select(kv => new AppWindowGroup(kv.Value.name, AppKey.Encode(kv.Value.path), (int)kv.Key,
                $"/api/icons/{AppKey.Encode(kv.Value.path)}", kv.Value.windows))
            .OrderBy(g => g.appName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The process that really owns a window. Packaged (Store) apps are hosted by ApplicationFrameHost.exe;
    /// their real process owns a CoreWindow child, so we look one level down.
    /// </summary>
    public static uint OwningProcess(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return 0;
        if (!string.Equals(Win32.GetClassName(hwnd), "ApplicationFrameWindow", StringComparison.Ordinal)) return pid;

        uint childPid = 0;
        EnumChildWindows(hwnd, (child, _) =>
        {
            Win32.GetWindowThreadProcessId(child, out var cp);
            if (cp != 0 && cp != pid) { childPid = cp; return false; }
            return true;
        }, IntPtr.Zero);
        return childPid != 0 ? childPid : pid;
    }

    public List<DisplayInfo> GetDisplays()
    {
        return Win32.EnumerateMonitors()
            .Select((m, i) => new DisplayInfo(m.Handle.ToInt64(), m.Bounds.Width, m.Bounds.Height,
                m.Primary ? $"Display {i + 1} (primary)" : $"Display {i + 1}", m.Bounds.Left, m.Bounds.Top, m.Primary))
            .ToList();
    }

    public static bool IsAlive(IntPtr hwnd) => Win32.IsWindow(hwnd);

    public List<IntPtr> WindowsForProcess(uint pid)
    {
        return Win32.EnumerateAltTabWindows().Where(h => OwningProcess(h) == pid).ToList();
    }

    /// <summary>Windows worth auto-streaming for an app: not minimized, larger than a toolbar, titled.</summary>
    public List<IntPtr> EligibleWindowsForProcess(uint pid)
    {
        return WindowsForProcess(pid).Where(h =>
        {
            if (Win32.IsIconic(h)) return false;
            var b = Win32.GetFrameBounds(h);
            return b.Width > 300 && b.Height > 200;
        }).ToList();
    }
}
