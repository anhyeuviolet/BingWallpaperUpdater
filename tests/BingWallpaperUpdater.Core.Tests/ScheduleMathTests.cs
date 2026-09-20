using BingWallpaperUpdater.Core.Scheduling;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// ROT-04 / ROT-06 due-time arithmetic (D-06, D-07, D-13): catch-up-once after a sleep gap, the backward clock jump
/// clamp, DST transitions (UTC math is immune), interval changes and the retry ladder. Pure: no LogSink, no temp dir.
/// </summary>
public sealed class ScheduleMathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ThirtyMinutes = TimeSpan.FromMinutes(30);

    [Fact]
    public void Constants_MatchTheLockedDesign()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), ScheduleMath.HeartbeatPeriod);
        Assert.InRange(ScheduleMath.ResumeDebounce, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));   // D-10
        Assert.Equal(TimeSpan.FromSeconds(5), ScheduleMath.MinLeadAfterIntervalChange);
        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)], ScheduleMath.RetryDelays);
    }

    [Fact]
    public void IsDue_NullNextDue_True()
    {
        Assert.True(ScheduleMath.IsDue(T0, null));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(480, true)]
    public void IsDue_BeforeAndAtAndAfter(int minutesAfterDue, bool expected)
    {
        Assert.Equal(expected, ScheduleMath.IsDue(T0.AddMinutes(minutesAfterDue), T0));
    }

    [Fact]
    public void Rearm_IsNowPlusInterval_NotNextDuePlusMultiples()
    {
        DateTimeOffset now = T0.AddHours(8).AddMinutes(7);   // the heartbeat wakes 8 h 7 min after the schedule was due

        DateTimeOffset next = ScheduleMath.Rearm(now, ThirtyMinutes);

        Assert.Equal(T0.AddHours(8).AddMinutes(37), next);
        for (int n = 1; n <= 20; n++)
        {
            Assert.NotEqual(T0.AddMinutes(30 * n), next);   // nextDue + n * interval would replay the 16 missed ticks
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Rearm_NonPositiveInterval_Throws(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleMath.Rearm(T0, TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void ClampAfterClockChange_BackwardJump3h_ClampsToNowPlusInterval()
    {
        // The clock jumped back 3 h, so a due time 10 min ahead is now 3 h 10 min ahead: more than one interval.
        DateTimeOffset now = T0;
        DateTimeOffset? nextDue = T0.AddHours(3).AddMinutes(10);

        DateTimeOffset? clamped = ScheduleMath.ClampAfterClockChange(now, nextDue, ThirtyMinutes);

        Assert.Equal(T0.AddMinutes(30), clamped);
    }

    [Theory]
    [InlineData(30)]    // exactly one interval ahead is allowed
    [InlineData(10)]
    [InlineData(0)]
    [InlineData(-180)]  // past due is not clamped; the heartbeat runs one catch-up instead
    public void ClampAfterClockChange_WithinInterval_Unchanged(int minutesAhead)
    {
        DateTimeOffset? nextDue = T0.AddMinutes(minutesAhead);

        Assert.Equal(nextDue, ScheduleMath.ClampAfterClockChange(T0, nextDue, ThirtyMinutes));
    }

    [Fact]
    public void ClampAfterClockChange_Null_Null()
    {
        Assert.Null(ScheduleMath.ClampAfterClockChange(T0, null, ThirtyMinutes));
    }

    [Fact]
    public void ClampAfterClockChange_IsIdempotent()
    {
        DateTimeOffset? once = ScheduleMath.ClampAfterClockChange(T0, T0.AddHours(5), ThirtyMinutes);
        DateTimeOffset? twice = ScheduleMath.ClampAfterClockChange(T0, once, ThirtyMinutes);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void AfterIntervalChange_LastAppliedPlusNew_WhenFarEnough()
    {
        DateTimeOffset now = T0;
        DateTimeOffset lastApplied = T0.AddMinutes(-10);

        DateTimeOffset next = ScheduleMath.AfterIntervalChange(lastApplied, now, TimeSpan.FromMinutes(60));

        Assert.Equal(T0.AddMinutes(50), next);
    }

    [Fact]
    public void AfterIntervalChange_ClampsToNowPlus5s_WhenAlreadyPast()
    {
        DateTimeOffset now = T0;
        DateTimeOffset lastApplied = T0.AddHours(-3);

        DateTimeOffset next = ScheduleMath.AfterIntervalChange(lastApplied, now, ThirtyMinutes);

        Assert.Equal(T0.AddSeconds(5), next);
    }

    [Fact]
    public void AfterIntervalChange_NullLastApplied_UsesNow()
    {
        Assert.Equal(T0.AddMinutes(120), ScheduleMath.AfterIntervalChange(null, T0, TimeSpan.FromMinutes(120)));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 15)]
    [InlineData(2, null)]
    [InlineData(5, null)]
    public void RetryDelay_Ladder_5_15_ThenNull(int stage, int? expectedMinutes)
    {
        TimeSpan? delay = ScheduleMath.RetryDelay(stage);

        Assert.Equal(expectedMinutes is { } m ? TimeSpan.FromMinutes(m) : null, delay);
    }

    [Fact]
    public void RetryDelay_NegativeStage_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleMath.RetryDelay(-1));
    }

    [Fact]
    public void Dst_FallBack_IntervalIsExactlyTwoHoursOfUtc()
    {
        // Central Europe leaves DST on 2026-10-25 at 01:00Z (03:00 CEST -> 02:00 CET): the local day has 25 hours.
        TimeZoneInfo cet = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        var start = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);

        DateTimeOffset next = ScheduleMath.Rearm(start, TimeSpan.FromMinutes(120));

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.Zero), next);
        Assert.Equal(TimeSpan.FromHours(2), next - start);   // UTC difference: exactly the interval

        DateTime localStart = TimeZoneInfo.ConvertTime(start, cet).DateTime;   // 02:30 CEST
        DateTime localNext = TimeZoneInfo.ConvertTime(next, cet).DateTime;     // 03:30 CET
        Assert.Equal(TimeSpan.FromHours(1), localNext - localStart);           // the wall clock only moved one hour
    }

    [Fact]
    public void Dst_SpringForward_SameUtcMath()
    {
        // Central Europe enters DST on 2026-03-29 at 01:00Z (02:00 CET -> 03:00 CEST): the local day has 23 hours.
        TimeZoneInfo cet = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        var start = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);

        DateTimeOffset next = ScheduleMath.Rearm(start, TimeSpan.FromMinutes(120));

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 2, 30, 0, TimeSpan.Zero), next);
        Assert.Equal(TimeSpan.FromHours(2), next - start);

        DateTime localStart = TimeZoneInfo.ConvertTime(start, cet).DateTime;   // 01:30 CET
        DateTime localNext = TimeZoneInfo.ConvertTime(next, cet).DateTime;     // 04:30 CEST
        Assert.Equal(TimeSpan.FromHours(3), localNext - localStart);           // the wall clock jumped three hours
    }
}
