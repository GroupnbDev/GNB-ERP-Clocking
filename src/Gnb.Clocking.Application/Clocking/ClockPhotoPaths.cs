using System.Globalization;

namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// Same disk convention as gnbSaasApi <c>PortalTimesheetDocumentHelper</c>:
/// <c>Candidates/{candidateId}/Records/{yyyy-MM-dd}/ClockIN.jpeg</c> or <c>ClockOut.jpeg</c>.
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

public sealed record ClockPhoto(string RelativePath, string AbsolutePath);
