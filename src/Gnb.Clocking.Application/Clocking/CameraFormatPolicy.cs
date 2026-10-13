namespace Gnb.Clocking.Application.Clocking;

/// <summary>One format a camera frame source offers.</summary>
public sealed record CameraFormatOption(uint Width, uint Height, double FrameRate, string Subtype);

/// <summary>
/// Picks the cheapest usable webcam format for the frame reader. Every frame is converted to BGRA on
/// the CPU, so a 1080p/30 fps default stream costs several times what the 640 px photo needs. Kept free
/// of WinRT so tests run anywhere.
/// </summary>
public static class CameraFormatPolicy
{
    public const uint MaxHeight = 480;
    public const double TargetFrameRate = 15;
    public const double MinFrameRate = 10;
    public const double MaxFrameRate = 30.5;

    /// <summary>
    /// Height ≤ <see cref="MaxHeight"/> (tallest first), frame rate in [10, 30] closest to 15, then
    /// NV12, YUY2, other subtypes, MJPG, and H264/HEVC/H265 last. The BGRA frame reader can convert
    /// MJPG; H264 often produces no bitmap and the watchdog then rebuilds forever. When nothing fits
    /// the height cap, the smallest in-range format wins. Null when no format is in the frame-rate range.
    /// </summary>
    public static CameraFormatOption? Choose(IEnumerable<CameraFormatOption> formats)
    {
        var usable = formats
            .Where(format => format.Height > 0
                && format.FrameRate >= MinFrameRate - 0.5
                && format.FrameRate <= MaxFrameRate)
            .ToList();
        if (usable.Count == 0)
            return null;

        var capped = usable.Where(format => format.Height <= MaxHeight).ToList();
        var ordered = capped.Count > 0
            ? capped.OrderByDescending(format => format.Height)
            : usable.OrderBy(format => format.Height);

        return ordered
            .ThenBy(format => Math.Abs(format.FrameRate - TargetFrameRate))
            .ThenBy(format => SubtypeRank(format.Subtype))
            .First();
    }

    private static int SubtypeRank(string subtype) =>
        subtype.ToUpperInvariant() switch
        {
            "NV12" => 0,
            "YUY2" => 1,
            "MJPG" => 3,
            "H264" or "HEVC" or "H265" => 4,
            _ => 2,
        };
}
