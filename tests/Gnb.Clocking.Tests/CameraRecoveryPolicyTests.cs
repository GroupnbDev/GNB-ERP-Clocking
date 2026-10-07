using Gnb.Clocking.Application.Clocking;
using Xunit;

namespace Gnb.Clocking.Tests;

public sealed class KioskRestartPolicyTests
{
    [Fact]
    public void A_long_run_that_exits_comes_back_in_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), KioskRestartPolicy.NextDelay(TimeSpan.FromHours(1), consecutiveFastExits: 1));
    }

    [Fact]
    public void Startup_crashes_back_off_and_then_wait_five_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), KioskRestartPolicy.NextDelay(TimeSpan.FromSeconds(1), 1));
        Assert.Equal(TimeSpan.FromSeconds(4), KioskRestartPolicy.NextDelay(TimeSpan.FromSeconds(1), 2));
        Assert.Equal(TimeSpan.FromSeconds(8), KioskRestartPolicy.NextDelay(TimeSpan.FromSeconds(1), 3));
        Assert.Equal(TimeSpan.FromMinutes(5), KioskRestartPolicy.NextDelay(TimeSpan.FromSeconds(1), 8));
    }
}

public sealed class CameraRecoveryPolicyTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 30)]
    [InlineData(4, 60)]
    [InlineData(40, 60)]
    public void Backoff_steps_then_stays_at_one_minute(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), CameraRecoveryPolicy.NextDelay(attempt));
    }

    [Fact]
    public void Daily_rebuild_is_due_once_after_the_clock_and_not_again_the_same_day()
    {
        var at = new TimeOnly(3, 0);
        var morning = new DateTime(2026, 10, 2, 3, 0, 0);
        Assert.True(CameraRecoveryPolicy.IsDailyRebuildDue(morning, lastRebuild: null, at));

        var done = morning.AddMinutes(1);
        Assert.False(CameraRecoveryPolicy.IsDailyRebuildDue(morning.AddHours(2), done, at));
        Assert.True(CameraRecoveryPolicy.IsDailyRebuildDue(morning.AddDays(1), done, at));
    }

    [Fact]
    public void Daily_rebuild_waits_until_the_scheduled_time()
    {
        var before = new DateTime(2026, 10, 2, 2, 59, 0);
        Assert.False(CameraRecoveryPolicy.IsDailyRebuildDue(before, lastRebuild: null, new TimeOnly(3, 0)));
    }

    [Fact]
    public void Empty_schedule_never_rebuilds()
    {
        Assert.False(CameraRecoveryPolicy.IsDailyRebuildDue(DateTime.Now, lastRebuild: null, at: null));
    }

    [Fact]
    public void Session_recycle_waits_until_fifty_minutes_and_skips_a_punch_in_progress()
    {
        Assert.False(CameraRecoveryPolicy.IsSessionRecycleDue(TimeSpan.FromMinutes(49), busy: false));
        Assert.True(CameraRecoveryPolicy.IsSessionRecycleDue(TimeSpan.FromMinutes(50), busy: false));
        Assert.False(CameraRecoveryPolicy.IsSessionRecycleDue(TimeSpan.FromMinutes(50), busy: true));
    }

    [Fact]
    public void A_repeated_fallback_hour_is_still_the_same_calendar_day()
    {
        var at = new TimeOnly(1, 30);
        var first = new DateTime(2026, 11, 1, 1, 30, 0);
        var again = new DateTime(2026, 11, 1, 1, 30, 0);
        Assert.True(CameraRecoveryPolicy.IsDailyRebuildDue(first, lastRebuild: null, at));
        Assert.False(CameraRecoveryPolicy.IsDailyRebuildDue(again, first, at));
    }
}
