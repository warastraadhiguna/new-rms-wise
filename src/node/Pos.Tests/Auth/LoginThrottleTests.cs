using Pos.Shared.Auth;

namespace Pos.Tests.Auth;

public class LoginThrottleTests
{
    private static readonly DateTime T0 = Utc("2026-11-20T03:00:00Z");

    private static LoginThrottleState Fail(LoginThrottleState state, int times, DateTime now)
    {
        for (var i = 0; i < times; i++)
            state = LoginThrottle.OnFailure(state, now);
        return state;
    }

    [Fact]
    public void New_state_is_not_locked()
    {
        Assert.False(LoginThrottle.IsLocked(LoginThrottleState.Empty, T0));
    }

    [Fact]
    public void Four_failures_do_not_lock()
    {
        var state = Fail(LoginThrottleState.Empty, 4, T0);

        Assert.Equal(4, state.FailedCount);
        Assert.False(LoginThrottle.IsLocked(state, T0));
    }

    [Fact]
    public void Fifth_consecutive_failure_locks_for_five_minutes()
    {
        var state = Fail(LoginThrottleState.Empty, 5, T0);

        Assert.Equal(T0.AddMinutes(5), state.LockedUntil);
        Assert.True(LoginThrottle.IsLocked(state, T0.AddMinutes(4).AddSeconds(59)));
        Assert.False(LoginThrottle.IsLocked(state, T0.AddMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(5), LoginThrottle.LockDuration);
        Assert.Equal(5, LoginThrottle.MaxFailures);
    }

    [Fact]
    public void Success_resets_the_count()
    {
        var state = Fail(LoginThrottleState.Empty, 4, T0);

        state = LoginThrottle.OnSuccess();
        state = Fail(state, 4, T0);

        Assert.False(LoginThrottle.IsLocked(state, T0));
    }

    [Fact]
    public void After_the_lock_expires_counting_starts_again()
    {
        var locked = Fail(LoginThrottleState.Empty, 5, T0);
        var later = T0.AddMinutes(6);

        var state = LoginThrottle.OnFailure(locked, later);

        Assert.Equal(1, state.FailedCount);
        Assert.False(LoginThrottle.IsLocked(state, later));
    }

    [Fact]
    public void Remaining_lock_time()
    {
        var state = Fail(LoginThrottleState.Empty, 5, T0);

        Assert.Equal(TimeSpan.FromMinutes(2), LoginThrottle.Remaining(state, T0.AddMinutes(3)));
        Assert.Equal(TimeSpan.Zero, LoginThrottle.Remaining(state, T0.AddMinutes(10)));
    }

    [Fact]
    public void Rejects_non_utc_time()
    {
        Assert.Throws<ArgumentException>(() =>
            LoginThrottle.OnFailure(LoginThrottleState.Empty, new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Local)));
    }
}
