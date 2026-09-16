using System.Diagnostics;
using System.Text.Json;

namespace WindowCast.Server.Discovery;

public sealed record InstalledApp(string name, string bundleID, string path);

/// <summary>
/// Installed apps from the Start menu (desktop and Store) via Get-StartApps, cached and refreshed in the
/// background. Launching goes through shell:AppsFolder, which works for both kinds of app.
/// </summary>
public sealed class AppDiscovery
{
    private readonly object _lock = new();
    private List<InstalledApp> _apps = new();
    private DateTime _loadedAt = DateTime.MinValue;
    private Task? _refresh;

    public List<InstalledApp> GetInstalledApps()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _loadedAt > TimeSpan.FromMinutes(10) && (_refresh is null || _refresh.IsCompleted))
                _refresh = Task.Run(Refresh);
            return _apps;
        }
    }

    public Task WarmUpAsync() => Task.Run(Refresh);

    private void Refresh()
    {
        var list = new List<InstalledApp>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Get-StartApps | Select-Object Name, AppID | ConvertTo-Json -Compress\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
            foreach (var item in items)
            {
                var name = item.GetProperty("Name").GetString();
                var appId = item.GetProperty("AppID").GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId)) continue;
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new InstalledApp(name, AppKey.Encode(appId), appId));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WindowCast: Get-StartApps failed: {ex.Message}");
        }

        lock (_lock)
        {
            _apps = list.OrderBy(a => a.name, StringComparer.OrdinalIgnoreCase).ToList();
            _loadedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Launches by decoded key (AppUserModelId or exe path). Returns the shell process; the app's own PID is found by window tracking.</summary>
    public static void Launch(string appIdOrPath)
    {
        var target = File.Exists(appIdOrPath) ? appIdOrPath : $"shell:AppsFolder\\{appIdOrPath}";
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false, CreateNoWindow = true });
    }

    /// <summary>Best-effort image path for an AppID that is a desktop shortcut target (contains a known-folder GUID or a path).</summary>
    public static string? ImagePathForAppId(string appId)
    {
        if (File.Exists(appId)) return appId;
        // Desktop apps enumerated by Get-StartApps look like "{6D809377-...}\Vendor\App\app.exe"
        var idx = appId.IndexOf('}');
        if (appId.StartsWith('{') && idx > 0)
        {
            var guid = appId[..(idx + 1)];
            var rest = appId[(idx + 1)..].TrimStart('\\');
            var root = guid.ToUpperInvariant() switch
            {
                "{6D809377-6AF0-444B-8957-A3773F02200E}" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "{F38BF404-1D43-42F2-9305-67DE0B28FC23}" => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}" => Environment.GetFolderPath(Environment.SpecialFolder.System),
                "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}" => Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                "{5E6C858F-0E22-4760-9AFE-EA3317B67173}" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "{905E63B6-C1BF-494E-B29C-65B732D3D21A}" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                _ => null,
            };
            if (root is not null)
            {
                var candidate = Path.Combine(root, rest);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
