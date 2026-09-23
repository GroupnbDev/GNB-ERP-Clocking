using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;

namespace Gnb.Clocking.Infrastructure.ClockKiosk;

/// <summary><c>GET api/clock-kiosk/badges/{rfid}</c>. The kiosk lists no sample people.</summary>
public sealed class HttpCandidateBadgeDirectory : ICandidateBadgeDirectory
{
    private readonly ClockKioskApiClient _api;
    private readonly KioskShiftCache _shifts;

    public HttpCandidateBadgeDirectory(ClockKioskApiClient api, KioskShiftCache shifts)
    {
        _api = api;
        _shifts = shifts;
    }

    public async Task<CandidateBadge?> FindByRfidAsync(string rfid, CancellationToken cancellationToken = default)
    {
        var normalized = RfidNormalizer.Normalize(rfid);
        if (normalized.Length == 0)
            return null;

        var badge = await _api.GetBadgeAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (badge == null)
            return null;

        _shifts.SetShift(badge.CandidateId, badge.IsOnShift ? badge.OpenClockIn : null);
        return new CandidateBadge(
            badge.CandidateId,
            badge.CandidateNumber,
            badge.FirstName ?? string.Empty,
            badge.LastName ?? string.Empty,
            string.IsNullOrWhiteSpace(badge.Rfid) ? normalized : badge.Rfid,
            Role: "Candidate",
            badge.Assignment ?? string.Empty,
            badge.ClientName ?? string.Empty,
            badge.Site ?? string.Empty,
            badge.TenantId,
            badge.OrganizationId,
            badge.CardNumber ?? string.Empty,
            badge.ShiftFinished,
            badge.FinishedClockIn,
            badge.FinishedClockOut);
    }

    public IReadOnlyList<CandidateBadge> ListLinkedBadges() => Array.Empty<CandidateBadge>();
}
