namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// When the watcher may treat a still-running kiosk as stuck. The window writes a heartbeat on the UI
/// thread. A native crash already exits the process; this covers a window that stops answering and
/// never exits, which the watcher would otherwise leave up forever.
/// </summary>
public static class KioskLivenessPolicy
{
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(3);

    /// <summary>
    /// True when the process has been up past <see cref="StartupGrace"/> and the last heartbeat is
    /// older than <see cref="StaleAfter"/>. A missing heartbeat is not proof: the file may be unwritable.
    /// </summary>
    public static bool IsUnresponsive(TimeSpan processUptime, TimeSpan? heartbeatAge)
    {
        if (processUptime < StartupGrace || heartbeatAge is not TimeSpan age)
            return false;

        return age > StaleAfter;
    }
}
