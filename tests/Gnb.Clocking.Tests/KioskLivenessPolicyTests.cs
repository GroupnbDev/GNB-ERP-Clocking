using Gnb.Clocking.Application.Clocking;
using Xunit;

namespace Gnb.Clocking.Tests;

public sealed class KioskLivenessPolicyTests
{
    [Fact]
    public void A_fresh_process_is_left_alone_even_with_an_old_heartbeat()
    {
        Assert.False(KioskLivenessPolicy.IsUnresponsive(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_recent_heartbeat_means_the_window_is_still_answering()
    {
        Assert.False(KioskLivenessPolicy.IsUnresponsive(TimeSpan.FromHours(2), TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void A_stale_heartbeat_after_startup_means_restart()
    {
        Assert.True(KioskLivenessPolicy.IsUnresponsive(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void A_missing_heartbeat_is_not_treated_as_a_hang()
    {
        Assert.False(KioskLivenessPolicy.IsUnresponsive(TimeSpan.FromHours(5), null));
    }
}
