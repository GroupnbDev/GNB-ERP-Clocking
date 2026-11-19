using System.Globalization;

namespace Gnb.Clocking.Infrastructure.ClockKiosk;

/// <summary>Local mirror of the server layout: <c>{root}/Users/{id}/Records/{day}/ClockIN.jpeg</c>.</summary>
internal static class ClockKioskPhotoPaths
{
    public static string LocalAbsolutePath(string root, int userId, string relativePath) =>
        Path.Combine(
            root,
            "Users",
            userId.ToString(CultureInfo.InvariantCulture),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Local file for a server photo path, or null when this kiosk has no copy (e.g. a portal punch).</summary>
    public static string? ExistingLocalPath(string root, int userId, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
            return null;
        var absolute = LocalAbsolutePath(root, userId, relativePath.TrimStart('/'));
        return File.Exists(absolute) ? absolute : null;
    }
}
