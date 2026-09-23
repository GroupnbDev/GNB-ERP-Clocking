namespace Gnb.Clocking.App.Controls;

public enum MotionLevel
{
    /// <summary>All effects at full frame rate.</summary>
    Full,

    /// <summary>Half frame rates, no glow shadows, fewer sparks. For older laptops.</summary>
    Lite,

    /// <summary>Static frames only. Phase changes still repaint once.</summary>
    Off
}

/// <summary>
/// How much motion the kiosk draws. Set from <c>ClockKiosk:Motion</c> (<c>Auto</c>, <c>Full</c>, <c>Lite</c>, <c>Off</c>).
/// <c>Auto</c> picks <see cref="MotionLevel.Lite"/> on modest hardware (4 or fewer logical cores, e.g. an
/// Intel i3, or 8 GB of RAM or less) or when the OS "reduce motion" setting is on, and
/// <see cref="MotionLevel.Full"/> otherwise. A running kiosk also steps down to Lite by itself when
/// frames keep missing their budget (<see cref="ReportOverBudget"/>).
/// </summary>
public static class MotionSettings
{
    public static MotionLevel Level { get; private set; } = MotionLevel.Full;

    public static bool IsLite => Level != MotionLevel.Full;

    /// <summary>Why <see cref="Level"/> was chosen, for the log.</summary>
    public static string Reason { get; private set; } = "default";

    /// <summary>Raised on the UI thread when the kiosk steps down at run time.</summary>
    public static event Action? Changed;

    private static bool _pinned;

    public static void Configure(string? configured)
    {
        var value = configured?.Trim().ToLowerInvariant();
        _pinned = value is "full" or "lite" or "low" or "reduced" or "off" or "none";
        (Level, Reason) = value switch
        {
            "full" => (MotionLevel.Full, "configured"),
            "lite" or "low" or "reduced" => (MotionLevel.Lite, "configured"),
            "off" or "none" => (MotionLevel.Off, "configured"),
            _ when SystemPrefersReducedMotion() => (MotionLevel.Lite, "OS reduce motion"),
            _ when IsModestHardware(out var why) => (MotionLevel.Lite, why),
            _ => (MotionLevel.Full, "auto"),
        };
    }

    /// <summary>
    /// Called by a canvas that has already stretched its frame rate to the limit and is still slow.
    /// In Auto mode the kiosk drops to Lite for the rest of the session so the reader stays responsive.
    /// </summary>
    public static void ReportOverBudget()
    {
        if (_pinned || Level != MotionLevel.Full)
            return;

        Level = MotionLevel.Lite;
        Reason = "frames over budget";
        Changed?.Invoke();
    }

    private static bool IsModestHardware(out string why)
    {
        var cores = Environment.ProcessorCount;
        var memoryGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        why = $"{cores} cores, {memoryGb:0.#} GB RAM";

        // 8 GB machines report a little under 8 once the GPU reserves its share.
        return cores <= 4 || (memoryGb > 0 && memoryGb <= 8.5);
    }

    private static bool SystemPrefersReducedMotion()
    {
        try
        {
#if MACCATALYST || IOS
            return UIKit.UIAccessibility.IsReduceMotionEnabled;
#elif WINDOWS
            return !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
#else
            return false;
#endif
        }
        catch
        {
            return false;
        }
    }
}
