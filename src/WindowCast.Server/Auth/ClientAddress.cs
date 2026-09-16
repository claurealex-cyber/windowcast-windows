namespace WindowCast.Server.Auth;

public static class ClientAddress
{
    /// <summary>
    /// The address used to key lockouts. Tailscale Serve proxies from loopback and sets X-Forwarded-For
    /// to the peer's tailnet IP; since we only listen on loopback that header is trustworthy.
    /// </summary>
    public static string Resolve(HttpContext ctx)
    {
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (first.Length > 0) return first;
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
