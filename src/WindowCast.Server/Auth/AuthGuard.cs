using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace WindowCast.Server.Auth;

public sealed record AuthError(int Status, string Message)
{
    public static readonly AuthError Unauthorized = new(401, "unauthorized");
    public static readonly AuthError TooManyAttempts = new(429, "Too many attempts, try again later");
}

/// <summary>
/// Bearer-token check with per-client lockout: 5 failures inside 60 s blocks that client for 300 s.
/// There is deliberately NO localhost bypass. Behind Tailscale Serve every request arrives from loopback,
/// so a bypass would open the server to the whole tailnet.
/// </summary>
public sealed class AuthGuard
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BlockDuration = TimeSpan.FromSeconds(300);

    private readonly TokenStore _tokens;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, FailureRecord> _failures = new();

    private sealed record FailureRecord(int Count, DateTimeOffset First);

    public AuthGuard(TokenStore tokens, TimeProvider? clock = null)
    {
        _tokens = tokens;
        _clock = clock ?? TimeProvider.System;
        _tokens.Rotated += ResetFailures;
    }

    /// <summary>Validates an Authorization header. Returns null when authorized.</summary>
    public AuthError? ValidateHeader(string? authorizationHeader, string clientAddress)
    {
        string? presented = null;
        if (authorizationHeader is not null && authorizationHeader.StartsWith("Bearer ", StringComparison.Ordinal))
            presented = authorizationHeader[7..].Trim();
        return ValidateToken(presented, clientAddress);
    }

    /// <summary>Validates a raw token (e.g. from a WebSocket query string). Returns null when authorized.</summary>
    public AuthError? ValidateToken(string? presented, string clientAddress)
    {
        var now = _clock.GetUtcNow();
        if (IsBlocked(clientAddress, now)) return AuthError.TooManyAttempts;

        if (presented is not null && TokensEqual(presented, _tokens.Current))
            return null;

        RecordFailure(clientAddress, now);
        return AuthError.Unauthorized;
    }

    public bool IsBlocked(string clientAddress) => IsBlocked(clientAddress, _clock.GetUtcNow());

    public void ResetFailures() => _failures.Clear();

    private bool IsBlocked(string clientAddress, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(clientAddress, out var record)) return false;
        if (record.Count < MaxFailures) return false;
        if (now - record.First < BlockDuration) return true;
        _failures.TryRemove(clientAddress, out _);
        return false;
    }

    private void RecordFailure(string clientAddress, DateTimeOffset now)
    {
        _failures.AddOrUpdate(
            clientAddress,
            _ => new FailureRecord(1, now),
            (_, rec) => now - rec.First > FailureWindow ? new FailureRecord(1, now) : rec with { Count = rec.Count + 1 });
    }

    private static bool TokensEqual(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
