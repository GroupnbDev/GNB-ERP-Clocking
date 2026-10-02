using Gnb.Clocking.Application.Clocking;
using Xunit;

namespace Gnb.Clocking.Tests;

public sealed class CameraWatchdogPolicyTests
{
    [Fact]
    public void Live_and_fresh_is_not_a_stall()
    {
        Assert.Equal(CameraWatchdogAction.None, CameraWatchdogPolicy.Evaluate(CameraHealth.Live, 999, 1000));
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(5001)]
    public void Live_at_or_past_the_stall_window_is_a_stall(long ageMs)
    {
        Assert.Equal(CameraWatchdogAction.Stall, CameraWatchdogPolicy.Evaluate(CameraHealth.Live, ageMs, 5000));
    }

    [Theory]
    [InlineData(CameraHealth.Starting)]
    [InlineData(CameraHealth.Recovering)]
    [InlineData(CameraHealth.Stalled)]
    [InlineData(CameraHealth.Unavailable)]
    public void Only_a_live_preview_can_stall(CameraHealth health)
    {
        Assert.Equal(CameraWatchdogAction.None, CameraWatchdogPolicy.Evaluate(health, 60_000, 5000));
    }
}
