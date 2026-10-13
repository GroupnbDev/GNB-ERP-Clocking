using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Gnb.Clocking.App.Diagnostics;

/// <summary>
/// A periodic resource line in the kiosk log. A process that dies silently after hours leaves this
/// trail, so the log shows whether memory, handles or camera restarts were climbing beforehand.
/// </summary>
public static class KioskHealth
{
    private const double Mb = 1024 * 1024;
    private static readonly long StartedAt = Stopwatch.GetTimestamp();
    private static int _cameraSessions;

    public static void CameraSessionStarted() => Interlocked.Increment(ref _cameraSessions);

    public static void Log(ILogger logger)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            logger.LogInformation(
                "Health uptime {Uptime} working set {WorkingSetMb:0} MB private {PrivateMb:0} MB managed {ManagedMb:0} MB handles {Handles} threads {Threads} camera sessions {CameraSessions}",
                Stopwatch.GetElapsedTime(StartedAt).ToString(@"d\.hh\:mm", System.Globalization.CultureInfo.InvariantCulture),
                process.WorkingSet64 / Mb,
                process.PrivateMemorySize64 / Mb,
                GC.GetTotalMemory(false) / Mb,
                process.HandleCount,
                process.Threads.Count,
                Volatile.Read(ref _cameraSessions));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Health line skipped");
        }
    }
}
