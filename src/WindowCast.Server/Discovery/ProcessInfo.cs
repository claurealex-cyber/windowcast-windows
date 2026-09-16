using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowCast.Server.Discovery;

/// <summary>Process name, image path and friendly product name, resilient to elevated processes.</summary>
public static class ProcessInfo
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    private static readonly Dictionary<uint, (string path, string name)> Cache = new();
    private static readonly object CacheLock = new();

    public static string? ImagePath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Returns (imagePath, friendlyName). Friendly name comes from the file's product/description, else the exe name.</summary>
    public static (string path, string name) Describe(uint pid)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(pid, out var cached)) return cached;
        }

        var path = ImagePath(pid) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        if (path.Length > 0)
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                var candidate = fvi.FileDescription;
                if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 40) candidate = fvi.ProductName;
                if (!string.IsNullOrWhiteSpace(candidate)) name = candidate.Trim();
            }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(name)) name = $"pid {pid}";

        lock (CacheLock)
        {
            if (Cache.Count > 512) Cache.Clear();
            Cache[pid] = (path, name);
        }
        return (path, name);
    }

    public static void Forget(uint pid)
    {
        lock (CacheLock) Cache.Remove(pid);
    }
}
