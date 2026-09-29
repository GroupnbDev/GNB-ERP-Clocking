namespace Gnb.Clocking.Application.Clocking;

public enum ClockCooldownKind
{
    /// <summary>Clocked in a moment ago: too soon to clock out.</summary>
    AfterClockIn,

    /// <summary>Clocked out a moment ago: too soon to tap again.</summary>
    AfterClockOut,
}

/// <param name="PunchedAt">The punch that blocks this tap.</param>
/// <param name="ReadyAt">When the next tap is allowed.</param>
public sealed record ClockCooldown(ClockCooldownKind Kind, DateTimeOffset PunchedAt, DateTimeOffset ReadyAt);

/// <summary>
/// How long a badge is held off after a punch. Pure so the rule can be tested without a kiosk:
/// the screen only formats what this returns.
/// </summary>
public static class ClockCooldownPolicy
{
    public const int AfterClockInMinutes = 30;
    public const int AfterClockOutMinutes = 15;

    public static readonly TimeSpan AfterClockIn = TimeSpan.FromMinutes(AfterClockInMinutes);
    public static readonly TimeSpan AfterClockOut = TimeSpan.FromMinutes(AfterClockOutMinutes);

    /// <param name="openClockIn">Start of the open shift, or null when they are off shift.</param>
    /// <param name="lastClockOut">
    /// The most recent clock-out this device knows about — from the server, or from a punch taken here
    /// that the server has not confirmed yet. Offline punches count: the hold is about the person in
    /// front of the reader, not about what the server has managed to record.
    /// </param>
    public static ClockCooldown? Evaluate(
        DateTimeOffset now,
        DateTimeOffset? openClockIn,
        DateTimeOffset? lastClockOut)
    {
        if (openClockIn is DateTimeOffset started && now - started < AfterClockIn)
            return new ClockCooldown(ClockCooldownKind.AfterClockIn, started, started + AfterClockIn);

        // An open shift that began after the clock-out means they are back on shift: nothing to hold.
        if (lastClockOut is DateTimeOffset ended
            && now - ended < AfterClockOut
            && (openClockIn is null || openClockIn.Value <= ended))
            return new ClockCooldown(ClockCooldownKind.AfterClockOut, ended, ended + AfterClockOut);

        return null;
    }

    /// <summary>A remembered clock-out is only worth keeping while it can still hold a tap.</summary>
    public static bool HasExpired(DateTimeOffset now, DateTimeOffset clockOut) =>
        now - clockOut >= AfterClockOut;
}
