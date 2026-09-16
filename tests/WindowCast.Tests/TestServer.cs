using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using WindowCast.Server.Hosting;

namespace WindowCast.Tests;

/// <summary>Starts a real Kestrel host on a test port range with an isolated token file.</summary>
public sealed class TestServer : IAsyncDisposable
{
    public const int PortStart = 18090;
    public const int PortEnd = 18095;

    public ServerHost Host { get; }
    public FakeTimeProvider Clock { get; }
    public string TokenPath { get; }
    public HttpClient Client { get; }

    private TestServer(ServerHost host, FakeTimeProvider clock, string tokenPath)
    {
        Host = host;
        Clock = clock;
        TokenPath = tokenPath;
        // 127.0.0.1 explicitly: "localhost" resolves to ::1 first and costs a ~2 s fallback per connection.
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
    }

    public static async Task<TestServer> StartAsync(int portStart = PortStart, int portEnd = PortEnd, FakeTimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        var dir = Path.Combine(Path.GetTempPath(), "windowcast-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var tokenPath = Path.Combine(dir, "token");
        var host = await ServerHost.StartAsync(new ServerOptions
        {
            PortStart = portStart,
            PortEnd = portEnd,
            TokenPath = tokenPath,
            Clock = clock,
            QuietLogging = true,
            SkipAppWarmup = true,
            WebRoot = FindWebRoot(),
        });
        return new TestServer(host, clock, tokenPath);
    }

    public HttpRequestMessage Request(HttpMethod method, string path, string? token, string? forwardedFor = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (forwardedFor is not null) req.Headers.Add("X-Forwarded-For", forwardedFor);
        return req;
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? token, string? forwardedFor = null)
        => Client.SendAsync(Request(HttpMethod.Get, path, token, forwardedFor));

    private static string FindWebRoot()
    {
        // The server project copies src/WindowCast.Web into its output as wwwroot; the test output has the same layout.
        var local = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(local)) return local;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "WindowCast.Web");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/WindowCast.Web");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(TokenPath)!, recursive: true); } catch { }
    }
}
