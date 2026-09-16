using Microsoft.Extensions.Time.Testing;
using WindowCast.Server.Auth;

namespace WindowCast.Tests;

public class AuthGuardTests
{
    private static (AuthGuard guard, TokenStore tokens, FakeTimeProvider clock) Make()
    {
        var path = Path.Combine(Path.GetTempPath(), "windowcast-tests", Guid.NewGuid().ToString("n"), "token");
        var tokens = new TokenStore(path);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        return (new AuthGuard(tokens, clock), tokens, clock);
    }

    [Fact]
    public void Correct_bearer_is_accepted()
    {
        var (guard, tokens, _) = Make();
        Assert.Null(guard.ValidateHeader($"Bearer {tokens.Current}", "10.0.0.1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic abc")]
    [InlineData("Bearer ")]
    [InlineData("Bearer nope")]
    public void Bad_headers_are_rejected(string? header)
    {
        var (guard, _, _) = Make();
        Assert.Equal(AuthError.Unauthorized, guard.ValidateHeader(header, "10.0.0.1"));
    }

    [Fact]
    public void Fifth_failure_locks_and_lock_expires_after_block_duration()
    {
        var (guard, _, clock) = Make();
        for (var i = 0; i < AuthGuard.MaxFailures; i++)
            Assert.Equal(AuthError.Unauthorized, guard.ValidateHeader("Bearer wrong", "10.0.0.1"));

        Assert.Equal(AuthError.TooManyAttempts, guard.ValidateHeader("Bearer wrong", "10.0.0.1"));
        Assert.True(guard.IsBlocked("10.0.0.1"));

        clock.Advance(AuthGuard.BlockDuration - TimeSpan.FromSeconds(1));
        Assert.True(guard.IsBlocked("10.0.0.1"));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(guard.IsBlocked("10.0.0.1"));
        Assert.Equal(AuthError.Unauthorized, guard.ValidateHeader("Bearer wrong", "10.0.0.1"));
    }

    [Fact]
    public void Failures_outside_the_window_do_not_accumulate()
    {
        var (guard, _, clock) = Make();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(AuthError.Unauthorized, guard.ValidateHeader("Bearer wrong", "10.0.0.1"));
            clock.Advance(TimeSpan.FromSeconds(61));
        }
        Assert.False(guard.IsBlocked("10.0.0.1"));
    }

    [Fact]
    public void Lockout_is_per_client()
    {
        var (guard, tokens, _) = Make();
        for (var i = 0; i < 6; i++) guard.ValidateHeader("Bearer wrong", "phone");
        Assert.True(guard.IsBlocked("phone"));
        Assert.False(guard.IsBlocked("mac"));
        Assert.Null(guard.ValidateHeader($"Bearer {tokens.Current}", "mac"));
    }

    [Fact]
    public void Blocked_client_is_refused_even_with_the_right_token()
    {
        var (guard, tokens, _) = Make();
        for (var i = 0; i < 5; i++) guard.ValidateHeader("Bearer wrong", "phone");
        Assert.Equal(AuthError.TooManyAttempts, guard.ValidateHeader($"Bearer {tokens.Current}", "phone"));
    }

    [Fact]
    public void Rotation_clears_lockouts_and_invalidates_old_token()
    {
        var (guard, tokens, _) = Make();
        var old = tokens.Current;
        for (var i = 0; i < 5; i++) guard.ValidateHeader("Bearer wrong", "phone");
        Assert.True(guard.IsBlocked("phone"));

        var fresh = tokens.Rotate();
        Assert.NotEqual(old, fresh);
        Assert.False(guard.IsBlocked("phone"));
        Assert.Equal(AuthError.Unauthorized, guard.ValidateHeader($"Bearer {old}", "phone"));
        Assert.Null(guard.ValidateHeader($"Bearer {fresh}", "phone"));
    }
}
