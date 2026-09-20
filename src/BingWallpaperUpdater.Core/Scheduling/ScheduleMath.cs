namespace BingWallpaperUpdater.Core.Scheduling;

/// <summary>
/// Pure due-time arithmetic for the scheduler (ROT-04, ROT-06; D-06, D-07, D-13). No I/O, no logging, no clock:
/// every method takes <c>now</c> from the caller, which reads it from the injected <see cref="TimeProvider"/> — never
/// from <c>DateTime.Now</c>, a tick counter or a stopwatch (CLAUDE.md "What NOT to Use"). <c>RotationService</c>
/// applies these rules; the tables in <c>ScheduleMathTests</c> prove them under sleep gaps, backward jumps and DST.
/// </summary>
public static class ScheduleMath
{
    /// <summary>How often the heartbeat compares the clock with the persisted due time (D-06).</summary>
    public static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(60);

    /// <summary>Delay applied by <c>Nudge</c> before the next heartbeat check after a resume/clock/network signal (D-10, within 5-10 s).</summary>
    public static readonly TimeSpan ResumeDebounce = TimeSpan.FromSeconds(8);

    /// <summary>An interval change never re-arms closer than this to <c>now</c> (D-07).</summary>
    public static readonly TimeSpan MinLeadAfterIntervalChange = TimeSpan.FromSeconds(5);

    /// <summary>Backoff ladder after a failed fetch: about 5 min, then about 15 min, then the normal interval (D-13).</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    /// <summary>True when the schedule is due: no due time persisted yet, or <paramref name="now"/> has reached it.</summary>
    public static bool IsDue(DateTimeOffset now, DateTimeOffset? nextDue) => nextDue is null || now >= nextDue.Value;

    /// <summary>
    /// The next due time after a tick: always <c>now + interval</c> (D-06). Never <c>nextDue + n * interval</c>, so an
    /// 8 h sleep over a 30 min interval yields exactly one catch-up tick and no burst.
    /// </summary>
    public static DateTimeOffset Rearm(DateTimeOffset now, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return now + interval;
    }

    /// <summary>
    /// Backward clock jump guard (D-07): a persisted due time more than one interval ahead of <paramref name="now"/>
    /// is pulled back to <c>now + interval</c>; anything else (including null) is returned unchanged. Idempotent, so
    /// the heartbeat can apply it on every beat.
    /// </summary>
    public static DateTimeOffset? ClampAfterClockChange(DateTimeOffset now, DateTimeOffset? nextDue, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return nextDue is { } due && due - now > interval ? now + interval : nextDue;
    }

    /// <summary>
    /// Due time after the user changes the interval (D-07, Phase 3 caller): <c>(lastApplied ?? now) + newInterval</c>,
    /// floored at <c>now + <see cref="MinLeadAfterIntervalChange"/></c> so a shorter interval that is already overdue
    /// fires soon but never in the past.
    /// </summary>
    public static DateTimeOffset AfterIntervalChange(DateTimeOffset? lastApplied, DateTimeOffset now, TimeSpan newInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(newInterval, TimeSpan.Zero);
        DateTimeOffset floor = now + MinLeadAfterIntervalChange;
        DateTimeOffset candidate = (lastApplied ?? now) + newInterval;
        return candidate < floor ? floor : candidate;
    }

    /// <summary>
    /// Delay before the next fetch retry for the given consecutive-failure stage (0-based), or null once the ladder
    /// is exhausted and the normal interval takes over (D-13).
    /// </summary>
    public static TimeSpan? RetryDelay(int failureStage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failureStage);
        return failureStage < RetryDelays.Length ? RetryDelays[failureStage] : null;
    }
}
