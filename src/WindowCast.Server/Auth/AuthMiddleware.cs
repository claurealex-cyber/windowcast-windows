namespace WindowCast.Server.Auth;

/// <summary>Guards every /api/* route except /api/icons/* (icons are public, like static files).</summary>
public sealed class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthGuard _guard;

    public AuthMiddleware(RequestDelegate next, AuthGuard guard)
    {
        _next = next;
        _guard = guard;
    }

    public async Task Invoke(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/icons"))
        {
            var error = _guard.ValidateHeader(ctx.Request.Headers.Authorization, ClientAddress.Resolve(ctx));
            if (error is not null)
            {
                ctx.Response.StatusCode = error.Status;
                await ctx.Response.WriteAsJsonAsync(new { error = error.Message });
                return;
            }
        }
        await _next(ctx);
    }
}
