namespace Gnb.Clocking.Application.Clocking;

public enum CameraWatchdogAction
{
    None,
    Stall
}

/// <summary>
/// Decides whether a live preview has stopped delivering frames. Starting and recovering
/// are not stalls: those states already have their own retry.
/// </summary>
public static class CameraWatchdogPolicy
{
    public static CameraWatchdogAction Evaluate(CameraHealth health, long lastFrameAgeMs, int stallMs)
    {
        if (health != CameraHealth.Live)
            return CameraWatchdogAction.None;

        var limit = stallMs < 0 ? 0 : stallMs;
        return lastFrameAgeMs >= limit
            ? CameraWatchdogAction.Stall
            : CameraWatchdogAction.None;
    }
}
