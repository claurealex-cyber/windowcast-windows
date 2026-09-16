using System.Text.Json;
using WindowCast.Server.Discovery;
using WindowCast.Server.Hosting;
using WindowCast.Server.Sessions;

namespace WindowCast.Server.Api;

/// <summary>REST surface, path-for-path with the macOS server so the browser client is shared.</summary>
public static class ApiRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/status", (SessionManager sm) => Results.Json(new
        {
            version = ServerHost.Version,
            platform = "windows",
            transport = "ws-jpeg",
            sessions = sm.Count,
            maxSessions = SessionManager.MaxConcurrentSessions,
        }));

        app.MapGet("/api/windows", (WindowDiscovery wd) => Results.Json(wd.GetWindows()));
        app.MapGet("/api/displays", (WindowDiscovery wd) => Results.Json(wd.GetDisplays()));
        app.MapGet("/api/apps", (AppDiscovery ad) => Results.Json(ad.GetInstalledApps()));

        // Icons are public, like static files, and cacheable.
        app.MapGet("/api/icons/{key}", (string key, HttpContext ctx) =>
        {
            var decoded = AppKey.Decode(key);
            var png = decoded is null || decoded.Length == 0 ? null : ShellIcons.GetPng(decoded);
            if (png is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "max-age=86400";
            return Results.File(png, "image/png");
        });

        app.MapGet("/api/sessions", (SessionManager sm) => Results.Json(sm.Infos()));

        app.MapPost("/api/sessions/create", async (HttpContext ctx, SessionManager sm) =>
        {
            var body = await ReadBody(ctx);
            if (!TryLong(body, "windowID", out var hwnd)) return Error(400, "Missing windowID");
            return Guard(() => sm.CreateWindowSession(new IntPtr(hwnd)).Info);
        });

        app.MapPost("/api/sessions/create/display", async (HttpContext ctx, SessionManager sm) =>
        {
            var body = await ReadBody(ctx);
            if (!TryLong(body, "displayID", out var hmon)) return Error(400, "Missing displayID");
            return Guard(() => sm.CreateDisplaySession(new IntPtr(hmon)).Info);
        });

        app.MapPost("/api/sessions/app", async (HttpContext ctx, SessionManager sm) =>
        {
            var body = await ReadBody(ctx);
            if (!TryLong(body, "pid", out var pid)) return Error(400, "Missing pid");
            return Guard(() => sm.CreateSessionsForApp((uint)pid).Select(s => s.Info).ToList());
        });

        app.MapMethods("/api/sessions/{id}/delete", new[] { "GET", "POST" }, (string id, SessionManager sm) =>
        {
            sm.Destroy(id);
            return Results.Json(new { deleted = true });
        });

        app.MapMethods("/api/sessions/{id}/activate", new[] { "GET", "POST" }, (string id, SessionManager sm) =>
        {
            sm.SetActive(id);
            return Results.Json(new { activated = true });
        });

        app.MapPost("/api/launch", async (HttpContext ctx) =>
        {
            var body = await ReadBody(ctx);
            var key = TryString(body, "bundleID");
            var target = key is null ? null : AppKey.Decode(key);
            if (string.IsNullOrEmpty(target)) return Error(400, "Missing bundleID");
            return Guard(() => { AppDiscovery.Launch(target); return new { launched = true, pid = 0 }; });
        });

        app.MapPost("/api/launch/track", async (HttpContext ctx, SessionManager sm) =>
        {
            var body = await ReadBody(ctx);
            var key = TryString(body, "bundleID");
            var target = key is null ? null : AppKey.Decode(key);
            if (string.IsNullOrEmpty(target)) return Error(400, "Missing bundleID");
            try
            {
                var session = await sm.LaunchAndTrackAsync(target);
                if (session is not null) return Results.Json(session.Info);
                return Results.Json(new SessionInfo("", 0, "", key!, 0, "closed", 0, 0, false, 0));
            }
            catch (SessionException ex) { return Error(ex.Status, ex.Message); }
            catch (Exception ex) { return Error(500, ex.Message); }
        });

        // Window management arrives in M3.
        foreach (var action in new[] { "resize", "maximize", "half", "restore" })
            app.MapPost($"/api/sessions/{{id}}/{action}", () => Error(501, "Window control arrives in M3"));

        // No WebRTC on this server; the client is told via /api/status to use the WebSocket transport.
        app.MapPost("/api/sessions/{id}/offer", () => Error(501, "WebRTC not available; use the WebSocket transport"));
        app.MapPost("/api/sessions/{id}/ice/send", () => Results.Text("ok"));
        app.MapGet("/api/sessions/{id}/ice/get", () => Results.Json(Array.Empty<object>()));
    }

    private static async Task<JsonElement> ReadBody(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.ContentLength is 0) return default;
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return default;
        }
    }

    private static bool TryLong(JsonElement body, string name, out long value)
    {
        value = 0;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out value)) return true;
        return p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out value);
    }

    private static string? TryString(JsonElement body, string name)
        => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static IResult Guard<T>(Func<T> work)
    {
        try { return Results.Json(work()); }
        catch (SessionException ex) { return Error(ex.Status, ex.Message); }
        catch (Exception ex) { return Error(500, ex.Message); }
    }
}
