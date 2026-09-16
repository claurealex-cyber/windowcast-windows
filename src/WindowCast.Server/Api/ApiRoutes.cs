using WindowCast.Server.Hosting;

namespace WindowCast.Server.Api;

public static class ApiRoutes
{
    public static void Map(WebApplication app)
    {
        // M0: contract-shaped stubs. Real discovery and sessions arrive in M2.
        app.MapGet("/api/status", () => Results.Json(new
        {
            version = ServerHost.Version,
            sessions = 0,
        }));
        app.MapGet("/api/sessions", () => Results.Json(Array.Empty<object>()));
        app.MapGet("/api/windows", () => Results.Json(Array.Empty<object>()));
        app.MapGet("/api/apps", () => Results.Json(Array.Empty<object>()));
        app.MapGet("/api/displays", () => Results.Json(Array.Empty<object>()));
    }
}
