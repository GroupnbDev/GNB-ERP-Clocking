using Gnb.Clocking.Domain.Clocking;

namespace Gnb.Clocking.Application.Clocking;

public interface IStaffBadgeDirectory
{
    Task<StaffBadge?> FindByRfidAsync(string rfid, CancellationToken cancellationToken = default);

    IReadOnlyList<StaffBadge> ListLinkedBadges();
}
