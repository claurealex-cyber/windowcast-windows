using System.Net;

namespace WindowCast.Server.Hosting;

public sealed class ServerOptions
{
    /// <summary>Loopback only. Remote access goes through Tailscale Serve, never a direct bind.</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;
    public int PortStart { get; init; } = 8090;
    public int PortEnd { get; init; } = 8095;
    public string? TokenPath { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public string WebRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    public bool QuietLogging { get; init; }
    /// <summary>Tests skip the Get-StartApps warm-up (spawns PowerShell).</summary>
    public bool SkipAppWarmup { get; init; }
}
