namespace Gnb.Clocking.Domain.Clocking;

public sealed record ClockEvent(
    int CandidateId,
    string CandidateName,
    string Initials,
    ClockAction Action,
    DateTimeOffset At,
    string Assignment,
    double? HoursWorked,
    string? PhotoRelativePath = null,
    string? PhotoAbsolutePath = null,
    /// <summary>Captured on this device during an outage and not yet accepted by the server.</summary>
    bool PendingSync = false,
    /// <summary>The server refused this queued punch; it needs a person rather than another retry.</summary>
    bool NeedsAttention = false,
    /// <summary>The server filed this as extra time, not as the day's clock in or clock out.</summary>
    bool IsExtra = false);
