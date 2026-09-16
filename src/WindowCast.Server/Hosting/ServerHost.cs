using Microsoft.AspNetCore.StaticFiles;
using WindowCast.Server.Api;
using WindowCast.Server.Auth;

namespace WindowCast.Server.Hosting;

/// <summary>
/// Owns one Kestrel instance bound to the first free port in the configured range.
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    public const string Version = "0.1.0-m0";
    private const string HtmlCsp = "default-src 'self'; connect-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob:";

    private readonly WebApplication _app;

    public int Port { get; }
    public TokenStore Tokens { get; }
    public AuthGuard Auth { get; }
    public string LocalUrl => $"http://localhost:{Port}";

    private ServerHost(WebApplication app, int port, TokenStore tokens, AuthGuard auth)
    {
        _app = app;
        Port = port;
        Tokens = tokens;
        Auth = auth;
    }

    public static async Task<ServerHost> StartAsync(ServerOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ServerOptions();
        var tokens = new TokenStore(options.TokenPath);
        var auth = new AuthGuard(tokens, options.Clock);

        Exception? last = null;
        for (var port = options.PortStart; port <= options.PortEnd; port++)
        {
            var app = Build(options, port, tokens, auth);
            try
            {
                await app.StartAsync(ct);
                return new ServerHost(app, port, tokens, auth);
            }
            catch (IOException ex)
            {
                last = ex;
                await app.DisposeAsync();
            }
        }
        throw new InvalidOperationException($"No free port in {options.PortStart}-{options.PortEnd}", last);
    }

    public Task WaitForShutdownAsync(CancellationToken ct = default) => _app.WaitForShutdownAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => _app.StopAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static WebApplication Build(ServerOptions options, int port, TokenStore tokens, AuthGuard auth)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = options.WebRoot,
        });

        builder.Logging.ClearProviders();
        if (!options.QuietLogging) builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(options.ListenAddress, port);
        });

        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton(options.Clock);
        builder.Services.AddSingleton(options);

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            await next();
        });

        app.UseMiddleware<AuthMiddleware>();

        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = c =>
            {
                if (c.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    c.Context.Response.Headers.ContentSecurityPolicy = HtmlCsp;
            },
        });

        ApiRoutes.Map(app);
        return app;
    }
}
