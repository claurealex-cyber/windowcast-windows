using System.Net;
using System.Net.Sockets;
using Xunit.Abstractions;

namespace WindowCast.Tests;

/// <summary>M0 stress gate. Every test here talks to a real Kestrel listener on 127.0.0.1.</summary>
[Collection("ports")]
public class ServerStressTests
{
    private readonly ITestOutputHelper _out;
    public ServerStressTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Index_is_served_with_csp_and_no_cache()
    {
        await using var s = await TestServer.StartAsync();
        var r = await s.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("WindowCast", html);
        Assert.Contains("default-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(r.Headers.CacheControl!.NoCache && r.Headers.CacheControl.NoStore && r.Headers.CacheControl.MustRevalidate);

        foreach (var f in new[] { "/app.js", "/style.css", "/manifest.json", "/sw.js" })
            Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync(f)).StatusCode);
    }

    [Fact]
    public async Task Api_requires_bearer_even_from_localhost()
    {
        await using var s = await TestServer.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.GetAsync("/api/sessions", token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/api/sessions", s.Host.Tokens.Current)).StatusCode);
    }

    [Fact]
    public async Task Thousand_bad_tokens_lock_after_five_and_unlock_after_300s()
    {
        await using var s = await TestServer.StartAsync();
        int unauthorized = 0, tooMany = 0, other = 0;

        for (var i = 0; i < 1000; i++)
        {
            var r = await s.GetAsync("/api/sessions", "wrong-token", forwardedFor: "100.64.0.9");
            switch ((int)r.StatusCode)
            {
                case 401: unauthorized++; break;
                case 429: tooMany++; break;
                default: other++; break;
            }
        }

        _out.WriteLine($"401={unauthorized} 429={tooMany} other={other}");
        Assert.Equal(5, unauthorized);
        Assert.Equal(995, tooMany);
        Assert.Equal(0, other);

        // Still blocked with the right token from that address, and another address is unaffected.
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await s.GetAsync("/api/sessions", s.Host.Tokens.Current, forwardedFor: "100.64.0.9")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await s.GetAsync("/api/sessions", s.Host.Tokens.Current, forwardedFor: "100.64.0.10")).StatusCode);

        s.Clock.Advance(TimeSpan.FromSeconds(301));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await s.GetAsync("/api/sessions", "wrong-token", forwardedFor: "100.64.0.9")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await s.GetAsync("/api/sessions", s.Host.Tokens.Current, forwardedFor: "100.64.0.9")).StatusCode);
    }

    [Fact]
    public async Task Rotating_token_invalidates_old_and_accepts_new()
    {
        await using var s = await TestServer.StartAsync();
        var old = s.Host.Tokens.Current;
        Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/api/status", old)).StatusCode);

        var fresh = s.Host.Tokens.Rotate();

        Assert.Equal(HttpStatusCode.Unauthorized, (await s.GetAsync("/api/status", old)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/api/status", fresh)).StatusCode);
        Assert.Equal(fresh, File.ReadAllText(s.TokenPath).Trim());
    }

    [Fact]
    public async Task Falls_through_to_next_port_when_first_is_busy()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, TestServer.PortStart);
        blocker.Start();
        try
        {
            await using var s = await TestServer.StartAsync();
            Assert.Equal(TestServer.PortStart + 1, s.Host.Port);
            Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/api/status", s.Host.Tokens.Current)).StatusCode);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task All_ports_busy_fails_clearly()
    {
        var blockers = new List<TcpListener>();
        for (var p = TestServer.PortStart; p <= TestServer.PortEnd; p++)
        {
            var l = new TcpListener(IPAddress.Loopback, p);
            l.Start();
            blockers.Add(l);
        }
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestServer.StartAsync());
            Assert.Contains("No free port", ex.Message);
        }
        finally
        {
            foreach (var l in blockers) l.Stop();
        }
    }

    [Fact]
    public async Task Start_and_stop_fifty_times_releases_the_port_every_time()
    {
        for (var i = 0; i < 50; i++)
        {
            int port;
            await using (var s = await TestServer.StartAsync())
            {
                port = s.Host.Port;
                Assert.Equal(TestServer.PortStart, port);
                Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/api/status", s.Host.Tokens.Current)).StatusCode);
            }

            // Port must be bindable again immediately after stop.
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
        }
    }
}

[CollectionDefinition("ports", DisableParallelization = true)]
public class PortsCollection { }
