namespace Pos.Shared.Auth;

/// <param name="FailedCount">Consecutive failures since the last success or the last lock.</param>
/// <param name="LockedUntil">UTC end of the current lock, if any.</param>
public readonly record struct LoginThrottleState(int FailedCount, DateTime? LockedUntil)
{
    public static LoginThrottleState Empty => new(0, null);
}

/// <summary>
/// Locks a login name for 5 minutes after 5 consecutive failed attempts (password or PIN).
/// The state is kept per login name, also for names that do not exist, so the behaviour never
/// reveals whether the username or the password was wrong.
/// </summary>
public static class LoginThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    public static bool IsLocked(LoginThrottleState state, DateTime now) => Remaining(state, now) > TimeSpan.Zero;

    public static TimeSpan Remaining(LoginThrottleState state, DateTime now)
    {
        EnsureUtc(now);
        return state.LockedUntil is { } until && until > now ? until - now : TimeSpan.Zero;
    }

    /// <summary>Call only when the attempt was not rejected for being locked.</summary>
    public static LoginThrottleState OnFailure(LoginThrottleState state, DateTime now)
    {
        EnsureUtc(now);
        var count = state.LockedUntil is not null ? 1 : state.FailedCount + 1;   // an expired lock starts a new round

        return count >= MaxFailures
            ? new LoginThrottleState(0, now + LockDuration)
            : new LoginThrottleState(count, null);
    }

    public static LoginThrottleState OnSuccess() => LoginThrottleState.Empty;

    private static void EnsureUtc(DateTime now)
    {
        if (now.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Time must be UTC.", nameof(now));
    }
}
