using Gnb.Clocking.Application.Clocking;
using Microsoft.Windows.System.Power;

namespace Gnb.Clocking.App.Platforms.Windows;

/// <summary>
/// Display, sleep and presence signals that can freeze a webcam while the process stays up.
/// </summary>
public static class CameraPowerSignals
{
    private static bool _attached;

    public static void Attach(Func<string, Task> rebuild, Func<CameraHealth> health)
    {
        if (_attached)
            return;

        _attached = true;
        PowerManager.DisplayStatusChanged += (_, _) =>
        {
            if (PowerManager.DisplayStatus == DisplayStatus.On)
                _ = rebuild("display-on");
        };
        PowerManager.SystemSuspendStatusChanged += (_, _) =>
        {
            var status = PowerManager.SystemSuspendStatus.ToString();
            if (status.Contains("Resume", StringComparison.OrdinalIgnoreCase))
                _ = rebuild("resume");
        };
        PowerManager.UserPresenceStatusChanged += (_, _) =>
        {
            if (PowerManager.UserPresenceStatus == UserPresenceStatus.Present && health() != CameraHealth.Live)
                _ = rebuild("user-present");
        };
    }
}
