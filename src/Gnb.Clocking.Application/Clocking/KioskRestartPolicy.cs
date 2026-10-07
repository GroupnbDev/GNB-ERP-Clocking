namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// How soon to open the kiosk again after the process exits. A long run that dies comes back
/// immediately. A build that crashes on startup backs off so it does not spin.
/// </summary>
public static class KioskRestartPolicy
{
    public static readonly TimeSpan HealthyUptime = TimeSpan.FromSeconds(30);

    public static TimeSpan NextDelay(TimeSpan uptime, int consecutiveFastExits)
    {
        if (uptime >= HealthyUptime)
            return TimeSpan.FromSeconds(2);

        if (consecutiveFastExits >= 8)
            return TimeSpan.FromMinutes(5);

        var seconds = 2;
        for (var attempt = 1; attempt < consecutiveFastExits; attempt++)
            seconds = Math.Min(60, seconds * 2);

        return TimeSpan.FromSeconds(seconds);
    }
}
