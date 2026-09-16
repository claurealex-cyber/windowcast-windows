using WindowCast.Server.Auth;
using WindowCast.Server.Sessions;

namespace WindowCast.Server.Api;

/// <summary>/ws/stream/{id}?token=… : JPEG frames down, input JSON up. Token is checked here (not by the /api middleware).</summary>
public static class StreamRoutes
{
    public static void Map(WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        app.Map("/ws/stream/{id}", async (HttpContext ctx, string id, SessionManager sm, AuthGuard guard) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "WebSocket expected" });
                return;
            }

            var address = ClientAddress.Resolve(ctx);
            var error = guard.ValidateToken(ctx.Request.Query["token"].FirstOrDefault(), address);
            if (error is not null)
            {
                ctx.Response.StatusCode = error.Status;
                await ctx.Response.WriteAsJsonAsync(new { error = error.Message });
                return;
            }

            var session = sm.Get(id);
            if (session is null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "Session not found" });
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var viewer = new StreamViewer(ws, address);
            session.AddViewer(viewer);
            Console.WriteLine($"WindowCast: viewer {viewer.Id} ({address}) attached to session {id}");
            try
            {
                await viewer.RunAsync(ctx.RequestAborted);
            }
            finally
            {
                session.RemoveViewer(viewer);
                Console.WriteLine($"WindowCast: viewer {viewer.Id} left session {id} (sent {viewer.FramesSent} frames)");
            }
        });
    }
}
