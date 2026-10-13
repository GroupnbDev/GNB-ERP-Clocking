using Foundation;

namespace Gnb.Clocking.App.Platforms.MacCatalyst;

/// <summary>
/// Keeps a Mac that is left running from idle-sleeping the display and dropping the camera.
/// The token has to stay referenced or the system ends the activity.
/// </summary>
internal static class KioskStayAwake
{
    private static NSObject? _activity;

    public static void Hold()
    {
        if (_activity != null)
            return;

        try
        {
            _activity = NSProcessInfo.ProcessInfo.BeginActivity(
                NSActivityOptions.UserInitiated
                | NSActivityOptions.IdleSystemSleepDisabled
                | NSActivityOptions.IdleDisplaySleepDisabled,
                "GroupNB Clock");
        }
        catch (Exception)
        {
            // The clock still runs if the system refuses the activity.
        }
    }
}
