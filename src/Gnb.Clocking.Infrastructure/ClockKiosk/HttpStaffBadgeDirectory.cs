using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;
using Gnb.Clocking.Infrastructure.ClockKiosk.Offline;

namespace Gnb.Clocking.Infrastructure.ClockKiosk;

/// <summary><c>GET api/clock-kiosk/badges/{rfid}</c>. The kiosk lists no sample people.</summary>
public sealed class HttpStaffBadgeDirectory : IStaffBadgeDirectory
{
    private readonly ClockKioskApiClient _api;
    private readonly KioskShiftCache _shifts;
    private readonly OfflineClockStore _store;

    public HttpStaffBadgeDirectory(ClockKioskApiClient api, KioskShiftCache shifts, OfflineClockStore store)
    {
        _api = api;
        _shifts = shifts;
        _store = store;
    }

    public async Task<StaffBadge?> FindByRfidAsync(string rfid, CancellationToken cancellationToken = default)
    {
        var normalized = RfidNormalizer.Normalize(rfid);
        if (normalized.Length == 0)
            return null;

        BadgeResponse? badge;
        try
        {
            badge = await _api.GetBadgeAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (ClockOfflineException)
        {
            // No link: name the person from the cached roster so the scan can still be captured.
            return await FromRosterAsync(normalized).ConfigureAwait(false);
        }

        if (badge == null)
            return null;

        QueuedPunch? queued = await _store.LatestPendingForUserAsync(badge.UserId).ConfigureAwait(false);
        // The server still shows them on shift until this clock-out syncs. Hold that out locally so a
        // second tap does not clock out again. A day the server already finished is left alone.
        bool coverPendingOut = queued is { IsClockOut: true } && badge.IsOnShift;
        if (coverPendingOut)
            _shifts.SetShift(badge.UserId, null);
        else
            _shifts.SetShift(badge.UserId, badge.IsOnShift ? badge.OpenClockIn : null);

        bool shiftFinished = badge.ShiftFinished;
        DateTimeOffset? finishedOut = coverPendingOut ? queued!.LocalTime : badge.FinishedClockOut;
        return new StaffBadge(
            badge.UserId,
            badge.EmployeeNumber ?? string.Empty,
            badge.FirstName ?? string.Empty,
            badge.LastName ?? string.Empty,
            string.IsNullOrWhiteSpace(badge.Rfid) ? normalized : badge.Rfid,
            Role: "Staff",
            badge.Assignment ?? string.Empty,
            badge.ClientName ?? string.Empty,
            badge.Site ?? string.Empty,
            badge.TenantId,
            badge.OrganizationId,
            badge.RfidNumber ?? string.Empty,
            shiftFinished,
            badge.FinishedClockIn,
            finishedOut);
    }

    /// <summary>
    /// Offline identity. Shift state comes from what this device last heard plus what it has queued,
    /// so a person who clocked in here during the outage is shown as on shift.
    /// </summary>
    private async Task<StaffBadge?> FromRosterAsync(string normalized)
    {
        RosterEntry? entry = await _store.FindRosterByRfidAsync(normalized).ConfigureAwait(false);
        if (entry == null)
            return null;

        // Shift state offline: the last snapshot from the server, then anything this device has queued
        // since. Without the snapshot a restart mid-outage turns a clock-out into a second clock-in.
        OpenShiftRecord? snapshot = await _store.FindOpenShiftAsync(entry.UserId).ConfigureAwait(false);
        DateTimeOffset? openAt = snapshot?.OpenClockIn;

        QueuedPunch? queued = await _store.LatestPendingForUserAsync(entry.UserId).ConfigureAwait(false);
        if (queued != null)
            openAt = queued.IsClockOut ? null : queued.LocalTime;

        _shifts.SetShift(entry.UserId, openAt);

        return new StaffBadge(
            entry.UserId,
            entry.EmployeeNumber,
            entry.FirstName,
            entry.LastName,
            normalized,
            Role: "Staff",
            entry.Assignment,
            entry.ClientName,
            Site: string.Empty,
            entry.TenantId,
            entry.OrganizationId,
            entry.RfidNumber,
            ShiftFinished: false,
            FinishedClockIn: null,
            FinishedClockOut: queued is { IsClockOut: true } ? queued.LocalTime : null);
    }

    public IReadOnlyList<StaffBadge> ListLinkedBadges() => Array.Empty<StaffBadge>();
}
