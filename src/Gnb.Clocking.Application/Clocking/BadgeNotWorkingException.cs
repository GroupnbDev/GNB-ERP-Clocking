namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// The card belongs to a candidate whose ERP status is not Working. Nothing is punched; the kiosk shows the
/// RFID number so staff can reassign the card.
/// </summary>
public sealed class BadgeNotWorkingException : ClockingException
{
    public BadgeNotWorkingException(string message, string rfid, string candidateName, string? status)
        : base(message)
    {
        Rfid = rfid;
        CandidateName = candidateName;
        Status = status;
    }

    public string Rfid { get; }

    public string CandidateName { get; }

    public string? Status { get; }
}
