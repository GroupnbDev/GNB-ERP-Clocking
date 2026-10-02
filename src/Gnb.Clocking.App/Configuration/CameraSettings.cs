using Microsoft.Extensions.Configuration;

namespace Gnb.Clocking.App.Configuration;

/// <summary>
/// Camera and log knobs from <c>ClockKiosk:*</c>, overridable as <c>ClockKiosk__*</c> in <c>.env</c>.
/// </summary>
public sealed class CameraSettings
{
    public const int DefaultStallSeconds = 5;
    public const int DefaultMaxPhotoAgeMs = 1000;
    public const int DefaultLogRetentionDays = 14;

    public int StallSeconds { get; }
    public int MaxPhotoAgeMs { get; }
    public TimeOnly? DailyRebuildAt { get; }
    public int LogRetentionDays { get; }

    public int StallMilliseconds => StallSeconds * 1000;

    public CameraSettings(int stallSeconds, int maxPhotoAgeMs, TimeOnly? dailyRebuildAt, int logRetentionDays)
    {
        StallSeconds = stallSeconds < 1 ? DefaultStallSeconds : stallSeconds;
        MaxPhotoAgeMs = maxPhotoAgeMs < 1 ? DefaultMaxPhotoAgeMs : maxPhotoAgeMs;
        DailyRebuildAt = dailyRebuildAt;
        LogRetentionDays = logRetentionDays < 1 ? DefaultLogRetentionDays : logRetentionDays;
    }

    public static CameraSettings Load(IConfiguration configuration)
    {
        var stall = ParseInt(configuration["ClockKiosk:CameraStallSeconds"], DefaultStallSeconds);
        var age = ParseInt(configuration["ClockKiosk:CameraMaxPhotoAgeMs"], DefaultMaxPhotoAgeMs);
        var keep = ParseInt(configuration["ClockKiosk:LogRetentionDays"], DefaultLogRetentionDays);
        return new CameraSettings(stall, age, ParseDaily(configuration["ClockKiosk:CameraDailyRebuildAt"]), keep);
    }

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var value) ? value : fallback;

    /// <summary>Missing key keeps 03:00. A blank value turns the daily rebuild off.</summary>
    private static TimeOnly? ParseDaily(string? raw)
    {
        if (raw == null)
            return new TimeOnly(3, 0);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return TimeOnly.TryParse(raw.Trim(), out var at) ? at : new TimeOnly(3, 0);
    }
}

/// <summary>Read by the camera handler without a DI constructor. MauiProgram assigns it.</summary>
public static class CameraSettingsAccess
{
    public static int StallMilliseconds { get; set; } = CameraSettings.DefaultStallSeconds * 1000;
    public static int MaxPhotoAgeMs { get; set; } = CameraSettings.DefaultMaxPhotoAgeMs;
}
