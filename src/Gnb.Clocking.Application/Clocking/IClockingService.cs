using Gnb.Clocking.Domain.Clocking;

namespace Gnb.Clocking.Application.Clocking;

public interface IClockingService
{
    int OpenCount { get; }

    /// <summary>Punches captured on this device that the server has not accepted yet.</summary>
    int PendingCount { get; }

    /// <summary>Queued punches the server refused on sync. They need a person, not another retry.</summary>
    int ParkedCount { get; }

    DateTimeOffset? GetOpenClockIn(int candidateId);

    IReadOnlyList<ClockEvent> GetEvents();

    /// <summary>Restores what the device already knows (open shifts, queue counts) with no network call.</summary>
    Task PrimeFromCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>Reloads open shifts and recent events from the clock server.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<ClockEvent> ClockInAsync(
        CandidateBadge candidate,
        ClockPhoto photo,
        bool resetCompletedDay = false,
        CancellationToken cancellationToken = default);

    Task<ClockEvent> ClockOutAsync(CandidateBadge candidate, ClockPhoto photo, CancellationToken cancellationToken = default);
}
