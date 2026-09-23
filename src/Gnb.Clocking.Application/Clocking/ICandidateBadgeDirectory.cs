using Gnb.Clocking.Domain.Clocking;

namespace Gnb.Clocking.Application.Clocking;

public interface ICandidateBadgeDirectory
{
    Task<CandidateBadge?> FindByRfidAsync(string rfid, CancellationToken cancellationToken = default);

    IReadOnlyList<CandidateBadge> ListLinkedBadges();
}
