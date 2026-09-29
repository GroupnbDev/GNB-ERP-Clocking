using Gnb.Clocking.Application.Clocking;
using Xunit;

namespace Gnb.Clocking.Tests;

/// <summary>
/// The hold after a punch: 30 minutes before a clock-in can be clocked out, 15 minutes before a
/// clocked-out badge is taken again.
/// </summary>
public sealed class ClockCooldownPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 17, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void A_tap_five_minutes_after_clocking_out_is_held()
    {
        var clockedOut = Now.AddMinutes(-5);

        ClockCooldown? hold = ClockCooldownPolicy.Evaluate(Now, openClockIn: null, lastClockOut: clockedOut);

        Assert.NotNull(hold);
        Assert.Equal(ClockCooldownKind.AfterClockOut, hold.Kind);
        Assert.Equal(clockedOut, hold.PunchedAt);
        Assert.Equal(clockedOut.AddMinutes(15), hold.ReadyAt);
    }

    [Theory]
    [InlineData(14.9, true)]
    [InlineData(15, false)]
    [InlineData(20, false)]
    public void The_clock_out_hold_lasts_fifteen_minutes(double minutesAgo, bool held)
    {
        ClockCooldown? hold = ClockCooldownPolicy.Evaluate(
            Now,
            openClockIn: null,
            lastClockOut: Now.AddMinutes(-minutesAgo));

        Assert.Equal(held, hold != null);
    }

    [Fact]
    public void An_unconfirmed_clock_out_taken_on_this_device_still_holds_the_badge()
    {
        // Offline the roster reports the day as unfinished, so the only record of the clock-out is the
        // one this device kept. It must still hold the next tap.
        ClockCooldown? hold = ClockCooldownPolicy.Evaluate(
            Now,
            openClockIn: null,
            lastClockOut: Now.AddMinutes(-2));

        Assert.NotNull(hold);
        Assert.Equal(ClockCooldownKind.AfterClockOut, hold.Kind);
    }

    [Fact]
    public void Clocking_back_in_after_that_clock_out_clears_the_hold()
    {
        var clockedOut = Now.AddMinutes(-10);
        var backOnShift = Now.AddMinutes(-9);

        // Inside the clock-in hold, so that one applies; the clock-out hold must not.
        ClockCooldown? hold = ClockCooldownPolicy.Evaluate(Now, backOnShift, clockedOut);

        Assert.NotNull(hold);
        Assert.Equal(ClockCooldownKind.AfterClockIn, hold.Kind);
    }

    [Fact]
    public void A_shift_open_longer_than_thirty_minutes_can_clock_out()
    {
        ClockCooldown? hold = ClockCooldownPolicy.Evaluate(
            Now,
            openClockIn: Now.AddHours(-3),
            lastClockOut: Now.AddHours(-9));

        Assert.Null(hold);
    }

    [Fact]
    public void A_fresh_clock_in_is_held_for_thirty_minutes()
    {
        var clockedIn = Now.AddMinutes(-4);

        ClockCooldown? hold = ClockCooldownPolicy.Evaluate(Now, clockedIn, lastClockOut: null);

        Assert.NotNull(hold);
        Assert.Equal(ClockCooldownKind.AfterClockIn, hold.Kind);
        Assert.Equal(clockedIn.AddMinutes(30), hold.ReadyAt);
    }

    [Fact]
    public void Nothing_is_held_for_a_badge_with_no_punches()
    {
        Assert.Null(ClockCooldownPolicy.Evaluate(Now, openClockIn: null, lastClockOut: null));
    }

    [Theory]
    [InlineData(14, false)]
    [InlineData(16, true)]
    public void A_remembered_clock_out_is_forgotten_once_it_can_no_longer_hold_a_tap(double minutesAgo, bool expired) =>
        Assert.Equal(expired, ClockCooldownPolicy.HasExpired(Now, Now.AddMinutes(-minutesAgo)));
}
