using Gnb.Clocking.Domain.Clocking;

namespace Gnb.Clocking.Infrastructure.ClockKiosk;

/// <summary>
/// Last known server state for the synchronous parts of <c>IClockingService</c>
/// (<c>OpenCount</c>, <c>GetOpenClockIn</c>, <c>GetEvents</c>). Filled by badge lookups,
/// punches and <c>GET api/clock-kiosk/sessions</c>.
/// </summary>
public sealed class KioskShiftCache
{
    private readonly object _gate = new();
    private readonly Dictionary<int, DateTimeOffset> _open = new();
    private IReadOnlyList<ClockEvent> _events = Array.Empty<ClockEvent>();

    public int OpenCount
    {
        get
        {
            lock (_gate)
                return _open.Count;
        }
    }

    public DateTimeOffset? GetOpenClockIn(int candidateId)
    {
        lock (_gate)
            return _open.TryGetValue(candidateId, out var at) ? at : null;
    }

    public IReadOnlyList<ClockEvent> GetEvents()
    {
        lock (_gate)
            return _events;
    }

    public void SetShift(int candidateId, DateTimeOffset? openClockIn)
    {
        lock (_gate)
        {
            if (openClockIn is DateTimeOffset at)
                _open[candidateId] = at;
            else
                _open.Remove(candidateId);
        }
    }

    public void Replace(IEnumerable<KeyValuePair<int, DateTimeOffset>> openShifts, IReadOnlyList<ClockEvent> events)
    {
        lock (_gate)
        {
            _open.Clear();
            foreach (var (candidateId, at) in openShifts)
                _open[candidateId] = at;
            _events = events;
        }
    }
}
