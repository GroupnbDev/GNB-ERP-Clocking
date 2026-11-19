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
    /// <summary>The badge is unknown or its user cannot clock yet. The RFID dialog shows its number.</summary>
    Unassigned
}
