using Gnb.Clocking.Application.Clocking;
using Xunit;

namespace Gnb.Clocking.Tests;

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
    public void A_repeated_fallback_hour_is_still_the_same_calendar_day()
    {
        var at = new TimeOnly(1, 30);
        var first = new DateTime(2026, 11, 1, 1, 30, 0);
        var again = new DateTime(2026, 11, 1, 1, 30, 0);
        Assert.True(CameraRecoveryPolicy.IsDailyRebuildDue(first, lastRebuild: null, at));
        Assert.False(CameraRecoveryPolicy.IsDailyRebuildDue(again, first, at));
    }
}
