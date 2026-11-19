namespace Gnb.Clocking.Domain.Clocking;

/// <summary>
/// A staff user matched by <c>users.rfid_number</c>.
/// <see cref="UserId"/> stays the database id used for punch rows and photo folders.
/// </summary>
public sealed record StaffBadge(
    int UserId,
    string EmployeeNumber,
    string FirstName,
    string LastName,
    string Rfid,
    string Role,
    string Assignment,
    string ClientName,
    string Site,
    int TenantId,
    int OrganizationId,
    string RfidNumber,
    bool ShiftFinished = false,
    DateTimeOffset? FinishedClockIn = null,
    DateTimeOffset? FinishedClockOut = null)
{
    public string FullName => $"{FirstName} {LastName}".Trim();

    public string Initials
    {
        get
        {
            var first = string.IsNullOrWhiteSpace(FirstName) ? "?" : FirstName[..1];
            var last = string.IsNullOrWhiteSpace(LastName) ? "?" : LastName[..1];
            return $"{first}{last}".ToUpperInvariant();
        }
    }
}
