using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;

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

    private readonly ClockKioskApiClient _api;
    private readonly KioskShiftCache _shifts;
    private readonly string _photoRoot;

    public HttpClockingService(ClockKioskApiClient api, KioskShiftCache shifts, ClockKioskApiOptions options)
    {
        _api = api;
        _shifts = shifts;
        _photoRoot = options.PhotoRoot;
    }

    public int OpenCount => _shifts.OpenCount;

    public DateTimeOffset? GetOpenClockIn(int candidateId) => _shifts.GetOpenClockIn(candidateId);

    public IReadOnlyList<ClockEvent> GetEvents() => _shifts.GetEvents();

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await _api.GetSessionsAsync(SessionTake, cancellationToken).ConfigureAwait(false);
        var events = (sessions.Events ?? new List<SessionEvent>())
            .Select(ToEvent)
            .OfType<ClockEvent>()
            .ToArray();
        var open = (sessions.OpenShifts ?? new List<OpenShift>())
            .Select(shift => new KeyValuePair<int, DateTimeOffset>(shift.CandidateId, shift.OpenClockIn));
        _shifts.Replace(open, events);
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

        var result = await _api.ClockAsync(isClockOut, candidate.Rfid, relative, resetCompletedDay, cancellationToken).ConfigureAwait(false);
        var at = new DateTimeOffset(DateTime.SpecifyKind(result.UtcTime, DateTimeKind.Utc));
        _shifts.SetShift(candidate.CandidateId, isClockOut ? null : at);

        var clockEvent = new ClockEvent(
            candidate.CandidateId,
            candidate.FullName,
            candidate.Initials,
            isClockOut ? ClockAction.Out : ClockAction.In,
            at,
            string.IsNullOrWhiteSpace(result.DemandName) ? candidate.Assignment : result.DemandName,
            isClockOut ? result.HoursWorked : null,
            result.ImagePath ?? relative,
            photo.AbsolutePath);

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
