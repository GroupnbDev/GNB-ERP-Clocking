namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// The badge belongs to a staff user who cannot clock yet (inactive, or no work schedule). Nothing is punched;
/// the kiosk tells the person to ask HR.
/// </summary>
public sealed class BadgeNotReadyException : ClockingException
{
    public BadgeNotReadyException(string message, string rfid, string userName, string? detail)
        : base(message)
    {
        Rfid = rfid;
        UserName = userName;
        Detail = detail;
    }

    public string Rfid { get; }

    public string UserName { get; }

    /// <summary>Why the badge is not ready: <c>inactive</c> or <c>no_schedule</c> (null when the server did not say).</summary>
    public string? Detail { get; }
}
