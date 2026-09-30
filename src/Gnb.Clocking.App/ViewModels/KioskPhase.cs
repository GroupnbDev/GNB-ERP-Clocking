namespace Gnb.Clocking.App.ViewModels;

public enum KioskPhase
{
    Idle,
    Reading,
    Capturing,
    Recognized,
    Hold,
    Success,
    Unknown,
    /// <summary>The card is not linked to a Working candidate. The RFID dialog shows its number.</summary>
    Unassigned
}
