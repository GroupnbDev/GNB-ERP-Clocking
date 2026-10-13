using Gnb.Clocking.Application.Clocking;
using Xunit;

namespace Gnb.Clocking.Tests;

public sealed class SupervisorSpawnPolicyTests
{
    [Fact]
    public void First_check_runs_immediately_then_every_30_seconds()
    {
        Assert.True(SupervisorSpawnPolicy.ShouldCheck(null));
        Assert.False(SupervisorSpawnPolicy.ShouldCheck(TimeSpan.FromSeconds(1)));
        Assert.False(SupervisorSpawnPolicy.ShouldCheck(TimeSpan.FromSeconds(29)));
        Assert.True(SupervisorSpawnPolicy.ShouldCheck(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void A_watcher_is_not_launched_again_within_a_minute()
    {
        Assert.True(SupervisorSpawnPolicy.MaySpawn(null));
        Assert.False(SupervisorSpawnPolicy.MaySpawn(TimeSpan.FromSeconds(30)));
        Assert.False(SupervisorSpawnPolicy.MaySpawn(TimeSpan.FromSeconds(59)));
        Assert.True(SupervisorSpawnPolicy.MaySpawn(TimeSpan.FromSeconds(60)));
    }
}
