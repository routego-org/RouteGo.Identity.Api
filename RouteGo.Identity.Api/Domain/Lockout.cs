namespace RouteGo.Identity.Api.Domain;

/// <summary>An account's failed-attempt window and lock.</summary>
/// <param name="FailedCount">Failed password attempts in the current window, 0 to 5.</param>
/// <param name="FirstFailedAt">When the current window started; null when there is none.</param>
/// <param name="LockedUntil">When the current lock ends; null when not locked.</param>
public sealed record LockoutState(int FailedCount, DateTimeOffset? FirstFailedAt, DateTimeOffset? LockedUntil);

/// <summary>
/// The lockout rule shared by sign-in and operator re-application: 5 failed password attempts
/// within 10 minutes lock the account for 15 minutes. While locked every attempt is refused — even
/// with the right password — and the lock is not extended. A successful sign-in clears the window.
/// </summary>
public static class Lockout
{
    private const int MaxFailures = 5;

    private static readonly TimeSpan _window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _lockDuration = TimeSpan.FromMinutes(15);

    /// <summary>The state after a successful sign-in: no failures, no lock.</summary>
    public static LockoutState Cleared { get; } = new LockoutState(0, null, null);

    /// <summary>Whether the account is locked at this moment.</summary>
    public static bool IsLocked(LockoutState state, DateTimeOffset now)
    {
        return state.LockedUntil.HasValue && state.LockedUntil.Value > now;
    }

    /// <summary>The state after one more failed password attempt on an account that is not locked.</summary>
    public static LockoutState RecordFailure(LockoutState state, DateTimeOffset now)
    {
        bool isWindowOpen = state.FirstFailedAt.HasValue && now - state.FirstFailedAt.Value <= _window;
        if (!isWindowOpen)
        {
            return new LockoutState(1, now, null);
        }

        int failedCount = state.FailedCount + 1;
        if (failedCount >= MaxFailures)
        {
            return new LockoutState(MaxFailures, state.FirstFailedAt, now + _lockDuration);
        }

        return new LockoutState(failedCount, state.FirstFailedAt, null);
    }
}
