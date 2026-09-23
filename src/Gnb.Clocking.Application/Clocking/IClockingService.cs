using Gnb.Clocking.Domain.Clocking;

namespace Gnb.Clocking.Application.Clocking;

public interface IClockingService
{
    int OpenCount { get; }

    DateTimeOffset? GetOpenClockIn(int candidateId);

    IReadOnlyList<ClockEvent> GetEvents();

    /// <summary>Reloads open shifts and recent events from the clock server.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<ClockEvent> ClockInAsync(
        CandidateBadge candidate,
        ClockPhoto photo,
        bool resetCompletedDay = false,
        CancellationToken cancellationToken = default);

    Task<ClockEvent> ClockOutAsync(CandidateBadge candidate, ClockPhoto photo, CancellationToken cancellationToken = default);
}
