using System.Globalization;

namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// Same disk convention as gnbSaasApi <c>PortalTimesheetDocumentHelper</c>:
/// the kiosk's local copy is <c>Users/{userId}/Records/{yyyy-MM-dd}/ClockIN.jpeg</c> or <c>ClockOut.jpeg</c>
/// until the upload response names the server path.
/// </summary>
public static class ClockPhotoPaths
{
    public const string RecordsRoot = "Records";
    public const string ClockInFileName = "ClockIN.jpeg";
    public const string ClockOutFileName = "ClockOut.jpeg";

    public static string FileName(bool isClockOut) =>
        isClockOut ? ClockOutFileName : ClockInFileName;

    public static string RelativePath(DateTime referenceDate, bool isClockOut)
    {
        var day = referenceDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{RecordsRoot}/{day}/{FileName(isClockOut)}";
    }
}

/// <param name="RelativePath">
/// The server's clock-photo path when <paramref name="OnServer"/> is true (sent back as <c>image_path</c>);
/// otherwise a local placeholder that is replaced by the upload response when the queued punch syncs.
/// </param>
public sealed record ClockPhoto(string RelativePath, string AbsolutePath, bool OnServer = false);
