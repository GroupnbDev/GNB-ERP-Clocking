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
    string? PhotoAbsolutePath = null);
