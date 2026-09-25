using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;
using Gnb.Clocking.Infrastructure.ClockKiosk.Offline;

namespace Gnb.Clocking.Infrastructure.ClockKiosk;

/// <summary>
/// Punches through <c>POST api/clock-kiosk/clock-in|clock-out</c>, which write
/// clock in/out on the existing punchcard day. They do not add a day column.
/// Open shifts and the session list come from
/// <c>GET api/clock-kiosk/sessions</c> plus badge lookups (see <see cref="KioskShiftCache"/>).
/// </summary>
public sealed class HttpClockingService : IClockingService
{
    public const int SessionTake = 50;

    /// <summary>How often the cached roster is pulled while the link is up.</summary>
    public static readonly TimeSpan RosterRefreshInterval = TimeSpan.FromMinutes(15);

    private readonly ClockKioskApiClient _api;
    private readonly KioskShiftCache _shifts;
    private readonly OfflineClockStore _store;
    private readonly ClockSyncWorker _sync;
    private readonly string _photoRoot;

    public HttpClockingService(
        ClockKioskApiClient api,
        KioskShiftCache shifts,
        ClockKioskApiOptions options,
        OfflineClockStore store,
        ClockSyncWorker sync)
    {
        _api = api;
        _shifts = shifts;
        _store = store;
        _sync = sync;
        _photoRoot = options.PhotoRoot;
    }

    public int OpenCount => _shifts.OpenCount;

    /// <summary>Punches captured on this device that the server has not accepted yet.</summary>
    public int PendingCount { get; private set; }

    /// <summary>Queued punches the server refused. These need a person, not another retry.</summary>
    public int ParkedCount { get; private set; }

    public DateTimeOffset? GetOpenClockIn(int candidateId) => _shifts.GetOpenClockIn(candidateId);

    /// <summary>
    /// Server events plus whatever this device is still holding, newest first. A punch taken during an
    /// outage has to appear here: otherwise the board says nobody clocked in while people are on shift.
    /// </summary>
    public IReadOnlyList<ClockEvent> GetEvents() =>
        _queued.Count == 0
            ? _shifts.GetEvents()
            : _queued
                .Concat(_shifts.GetEvents())
                .OrderByDescending(e => e.At)
                .Take(SessionTake)
                .ToArray();

    private IReadOnlyList<ClockEvent> _queued = Array.Empty<ClockEvent>();

    /// <summary>Rebuilds the "not synced yet" part of the feed from the queue.</summary>
    private async Task ReloadQueuedEventsAsync()
    {
        List<QueuedPunch> rows = await _store.ListUnsyncedAsync(SessionTake).ConfigureAwait(false);
        _queued = rows.Select(ToQueuedEvent).ToArray();
    }

    private static ClockEvent ToQueuedEvent(QueuedPunch punch)
    {
        var parked = punch.State == QueuedPunchStates.Parked;
        var name = string.IsNullOrWhiteSpace(punch.CandidateName) ? punch.CardNumber : punch.CandidateName;
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length >= 2
            ? $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
            : name.Length > 0 ? name[..1].ToUpperInvariant() : "?";

        return new ClockEvent(
            punch.CandidateId,
            name,
            initials,
            punch.IsClockOut ? ClockAction.Out : ClockAction.In,
            punch.LocalTime,
            punch.Assignment,
            HoursWorked: null,
            punch.PhotoRelativePath,
            File.Exists(punch.PhotoPath) ? punch.PhotoPath : null,
            PendingSync: !parked,
            NeedsAttention: parked);
    }

    /// <summary>
    /// Loads what the device already knows before any network call: who was on shift at the last
    /// successful sync, and how many punches are waiting. Called at startup so an offline restart still
    /// knows to clock people out instead of clocking them in again.
    /// </summary>
    public async Task PrimeFromCacheAsync(CancellationToken cancellationToken = default)
    {
        List<OpenShiftRecord> openShifts = await _store.ListOpenShiftsAsync().ConfigureAwait(false);
        foreach (OpenShiftRecord shift in openShifts)
        {
            if (shift.OpenClockIn is DateTimeOffset at)
                _shifts.SetShift(shift.CandidateId, at);
        }

        // A queued punch is newer than that snapshot, so it wins.
        foreach (QueuedPunch punch in await _store.ListPendingAsync().ConfigureAwait(false))
            _shifts.SetShift(punch.CandidateId, punch.IsClockOut ? null : punch.LocalTime);

        PendingCount = await _store.CountPendingAsync().ConfigureAwait(false);
        ParkedCount = await _store.CountParkedAsync().ConfigureAwait(false);
        await ReloadQueuedEventsAsync().ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Send anything captured during an outage before reading state back, so the list agrees with the server.
        SyncOutcome outcome = await _sync.DrainAsync(cancellationToken).ConfigureAwait(false);
        PendingCount = outcome.Pending;
        ParkedCount = await _store.CountParkedAsync().ConfigureAwait(false);
        await ReloadQueuedEventsAsync().ConfigureAwait(false);

        await RefreshRosterIfStaleAsync(cancellationToken).ConfigureAwait(false);

        var sessions = await _api.GetSessionsAsync(SessionTake, cancellationToken).ConfigureAwait(false);
        var events = (sessions.Events ?? new List<SessionEvent>())
            .Select(ToEvent)
            .OfType<ClockEvent>()
            .ToArray();
        var open = (sessions.OpenShifts ?? new List<OpenShift>())
            .Select(shift => new KeyValuePair<int, DateTimeOffset>(shift.CandidateId, shift.OpenClockIn));
        var openList = open.ToList();
        _shifts.Replace(openList, events);
        // Survive a restart during an outage: without this the next scan looks like a clock-in.
        await _store.ReplaceOpenShiftsAsync(openList).ConfigureAwait(false);
    }

    public Task<ClockEvent> ClockInAsync(
        CandidateBadge candidate,
        ClockPhoto photo,
        bool resetCompletedDay = false,
        CancellationToken cancellationToken = default) =>
        PunchAsync(candidate, photo, isClockOut: false, resetCompletedDay, cancellationToken);

    public Task<ClockEvent> ClockOutAsync(CandidateBadge candidate, ClockPhoto photo, CancellationToken cancellationToken = default) =>
        PunchAsync(candidate, photo, isClockOut: true, resetCompletedDay: false, cancellationToken);

    private async Task<ClockEvent> PunchAsync(
        CandidateBadge candidate,
        ClockPhoto photo,
        bool isClockOut,
        bool resetCompletedDay,
        CancellationToken cancellationToken)
    {
        var relative = photo?.RelativePath?.Replace('\\', '/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(relative)
            || !relative.EndsWith(ClockPhotoPaths.FileName(isClockOut), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(photo?.AbsolutePath))
            throw new ClockingException("A photo is required to clock in or out.");

        var clientPunchId = Guid.NewGuid().ToString("N");
        var capturedAt = DateTimeOffset.Now;
        ClockActionResponse result;
        try
        {
            result = await _api.ClockAsync(
                    isClockOut,
                    candidate.Rfid,
                    relative,
                    resetCompletedDay,
                    clientPunchId,
                    capturedAt,
                    capturedOffline: false,
                    await _store.GetClockSkewSecondsAsync().ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ClockOfflineException)
        {
            // Keep the punch on the device with its own id, so the drain cannot double it later.
            return await QueueAsync(candidate, photo, isClockOut, clientPunchId, capturedAt, relative).ConfigureAwait(false);
        }

        var at = new DateTimeOffset(DateTime.SpecifyKind(result.UtcTime, DateTimeKind.Utc));

        // Report what the server saved, not what this kiosk asked for. They differ when the device's
        // idea of the shift was stale, or when the punch landed on extra time instead of the day's pair.
        var savedAction = result.Action switch
        {
            "clock_in" => ClockAction.In,
            "clock_out" => ClockAction.Out,
            _ => isClockOut ? ClockAction.Out : ClockAction.In,
        };
        // Extra time does not open or close the day's shift, so it must not move the on-shift state.
        if (!result.IsExtra)
            _shifts.SetShift(candidate.CandidateId, savedAction == ClockAction.Out ? null : at);

        var clockEvent = new ClockEvent(
            candidate.CandidateId,
            candidate.FullName,
            candidate.Initials,
            savedAction,
            at,
            string.IsNullOrWhiteSpace(result.DemandName) ? candidate.Assignment : result.DemandName,
            savedAction == ClockAction.Out ? result.HoursWorked : null,
            result.ImagePath ?? relative,
            photo.AbsolutePath,
            IsExtra: result.IsExtra);

        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ClockingException)
        {
            // The punch is saved; the list catches up on the next refresh.
        }

        return clockEvent;
    }

    /// <summary>The roster only has to be fresh enough to name whoever taps during the next outage.</summary>
    private async Task RefreshRosterIfStaleAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? syncedAt = await _store.GetRosterSyncedAtAsync().ConfigureAwait(false);
        if (syncedAt is DateTimeOffset last && DateTimeOffset.Now - last < RosterRefreshInterval)
            return;

        try
        {
            await _sync.RefreshRosterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ClockingException)
        {
            // Best effort. A stale roster only limits offline scans; it must not stop the live screen
            // from loading sessions or reporting the server as reachable.
        }
    }

    /// <summary>
    /// Offline punch: stored on this device and reported to the screen as saved. The sync worker sends
    /// it with the same id and the original tap time, so the punch card shows when they actually tapped.
    /// </summary>
    private async Task<ClockEvent> QueueAsync(
        CandidateBadge candidate,
        ClockPhoto photo,
        bool isClockOut,
        string clientPunchId,
        DateTimeOffset capturedAt,
        string relativePath)
    {
        await _store.EnqueueAsync(new QueuedPunch
        {
            ClientPunchId = clientPunchId,
            CandidateId = candidate.CandidateId,
            CardNumber = candidate.Rfid,
            CandidateName = candidate.FullName,
            Assignment = candidate.Assignment,
            IsClockOut = isClockOut,
            LocalTime = capturedAt,
            PhotoPath = photo.AbsolutePath,
            PhotoRelativePath = relativePath,
            State = QueuedPunchStates.Pending,
            ClockSkewSeconds = await _store.GetClockSkewSecondsAsync().ConfigureAwait(false),
            CapturedAt = capturedAt,
        }).ConfigureAwait(false);

        _shifts.SetShift(candidate.CandidateId, isClockOut ? null : capturedAt);
        PendingCount = await _store.CountPendingAsync().ConfigureAwait(false);
        await ReloadQueuedEventsAsync().ConfigureAwait(false);

        return new ClockEvent(
            candidate.CandidateId,
            candidate.FullName,
            candidate.Initials,
            isClockOut ? ClockAction.Out : ClockAction.In,
            capturedAt,
            candidate.Assignment,
            HoursWorked: null,
            relativePath,
            photo.AbsolutePath,
            PendingSync: true);
    }

    private ClockEvent? ToEvent(SessionEvent session)
    {
        var action = session.Action switch
        {
            "clock_in" => ClockAction.In,
            "clock_out" => ClockAction.Out,
            _ => (ClockAction?)null,
        };
        if (action == null)
            return null;

        var first = session.FirstName ?? string.Empty;
        var last = session.LastName ?? string.Empty;
        var name = string.IsNullOrWhiteSpace(session.CandidateName) ? $"{first} {last}".Trim() : session.CandidateName;
        return new ClockEvent(
            session.CandidateId,
            name,
            Initials(first, last),
            action.Value,
            session.LocalTime,
            session.Assignment ?? string.Empty,
            action == ClockAction.Out ? session.HoursWorked : null,
            session.PhotoRelativePath,
            ClockKioskPhotoPaths.ExistingLocalPath(_photoRoot, session.CandidateId, session.PhotoRelativePath));
    }

    private static string Initials(string first, string last)
    {
        var a = string.IsNullOrWhiteSpace(first) ? "?" : first[..1];
        var b = string.IsNullOrWhiteSpace(last) ? "?" : last[..1];
        return $"{a}{b}".ToUpperInvariant();
    }
}
