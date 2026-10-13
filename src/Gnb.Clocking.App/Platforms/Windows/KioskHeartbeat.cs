using System.Globalization;
using Gnb.Clocking.Application.Clocking;
using Microsoft.UI.Dispatching;

namespace Gnb.Clocking.App.Platforms.Windows;

/// <summary>
/// Writes the heartbeat on the window thread. If that thread stops, the watcher sees a stale
/// timestamp and opens the kiosk again. The timer is rooted here so it is not collected.
/// </summary>
internal static class KioskUiPulse
{
    private static DispatcherQueueTimer? _timer;

    public static void Start()
    {
        KioskHeartbeat.Touch();
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue == null || _timer != null)
            return;

        _timer = queue.CreateTimer();
        _timer.Interval = KioskLivenessPolicy.HeartbeatEvery;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) =>
        {
            KioskStayAwake.Hold();
            KioskHeartbeat.Touch();
        };
        _timer.Start();
    }
}

/// <summary>
/// One UTC timestamp written from the window thread. The watcher reads it from the other process.
/// </summary>
internal static class KioskHeartbeat
{
    public static void Touch()
    {
        try
        {
            var path = Path();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            // A missing file is not a hang. The watcher only restarts when a timestamp goes stale.
        }
    }

    public static DateTimeOffset? ReadUtc()
    {
        try
        {
            var text = File.ReadAllText(Path()).Trim();
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Path() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GroupNB Clock",
            "kiosk.heartbeat");
}
