namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// Retry spacing and the once-a-day idle rebuild. Both stay free of the camera so tests can run
/// without WinRT.
/// </summary>
public static class CameraRecoveryPolicy
{
    /// <summary>
    /// How long one camera session may run before it is replaced. Webcam drivers on older laptops
    /// drop the process around the hour mark if the same session is left open.
    /// </summary>
    public static readonly TimeSpan SessionRecycleAfter = TimeSpan.FromMinutes(50);

    public static bool IsSessionRecycleDue(TimeSpan sessionAge, bool busy)
    {
        if (busy)
            return false;

        return sessionAge >= SessionRecycleAfter;
    }

    public static TimeSpan NextDelay(int attempt)
    {
        if (attempt <= 1)
            return TimeSpan.FromSeconds(5);
        if (attempt == 2)
            return TimeSpan.FromSeconds(15);
        if (attempt == 3)
            return TimeSpan.FromSeconds(30);
        return TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Due once the local clock has passed <paramref name="at"/> today, and not again until the next
    /// calendar day. A fall-back hour that repeats still counts as the same day, so the rebuild does
    /// not run twice. An empty <paramref name="at"/> disables it.
    /// </summary>
    public static bool IsDailyRebuildDue(DateTime now, DateTime? lastRebuild, TimeOnly? at)
    {
        if (at is null)
            return false;

        var scheduled = now.Date.Add(at.Value.ToTimeSpan());
        if (now < scheduled)
            return false;

        return lastRebuild is not DateTime done || done.Date != now.Date || done < scheduled;
    }
}
