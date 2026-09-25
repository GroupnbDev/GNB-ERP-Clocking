using System.Net;
using System.Text;
using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;
using Gnb.Clocking.Infrastructure.ClockKiosk;
using Gnb.Clocking.Infrastructure.ClockKiosk.Offline;
using Xunit;

namespace Gnb.Clocking.Tests;

/// <summary>
/// The outage path: a punch taken while the link is down must survive a restart, sync once with its
/// original tap time, and never be sent twice.
/// </summary>
public sealed class OfflineSyncTests : IAsyncDisposable
{
    private const string Key = "test-kiosk-key-0123456789abcdef0123";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnb-offline-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _handler = new();
    private OfflineClockStore? _store;

    private string DbPath => Path.Combine(_root, "clock-queue.db3");

    private OfflineClockStore Store()
    {
        Directory.CreateDirectory(_root);
        return _store ??= new OfflineClockStore(DbPath);
    }

    private ClockKioskApiOptions Options() => new()
    {
        BaseUrl = "http://clock.test",
        ApiKey = Key,
        PhotoRoot = _root,
    };

    private ClockKioskApiClient Api() => new(new HttpClient(_handler), Options());

    public async ValueTask DisposeAsync()
    {
        if (_store != null)
            await _store.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private QueuedPunch Punch(string id, bool isClockOut = false, DateTimeOffset? at = null)
    {
        Directory.CreateDirectory(_root);
        var photo = Path.Combine(_root, $"{id}.jpg");
        File.WriteAllBytes(photo, new byte[] { 0xFF, 0xD8, 0xFF });
        var when = at ?? new DateTimeOffset(2026, 9, 25, 8, 25, 0, TimeSpan.FromHours(-4));
        return new QueuedPunch
        {
            ClientPunchId = id,
            CandidateId = 1042,
            CardNumber = "04A1C8E291",
            CandidateName = "Maya Chen",
            IsClockOut = isClockOut,
            LocalTime = when,
            CapturedAt = when,
            PhotoPath = photo,
            PhotoRelativePath = ClockPhotoPaths.RelativePath(when.Date, isClockOut),
        };
    }

    [Fact]
    public async Task A_queued_punch_survives_a_restart()
    {
        var store = Store();
        await store.EnqueueAsync(Punch("punch-1"));
        await store.DisposeAsync();
        _store = null;

        // A second store over the same file is what the app does after a power cut.
        var reopened = Store();
        List<QueuedPunch> pending = await reopened.ListPendingAsync();

        Assert.Single(pending);
        Assert.Equal("punch-1", pending[0].ClientPunchId);
        Assert.Equal(1, await reopened.CountPendingAsync());
    }

    [Fact]
    public async Task Sync_sends_the_original_tap_time_and_the_punch_id()
    {
        var store = Store();
        var tapped = new DateTimeOffset(2026, 9, 25, 8, 25, 0, TimeSpan.FromHours(-4));
        await store.EnqueueAsync(Punch("punch-1", at: tapped));
        _handler.On("POST", "/api/clock-kiosk/photos", HttpStatusCode.OK,
            """{"relative_path":"Records/2026-09-25/ClockIN.jpeg","file_name":"ClockIN.jpeg"}""");
        _handler.On("POST", "/api/clock-kiosk/clock-in", HttpStatusCode.OK,
            """{"action":"clock_in","candidate_id":1042,"record_id":77,"utc_time":"2026-09-25T12:25:00Z"}""");

        SyncOutcome outcome = await new ClockSyncWorker(Api(), store).DrainAsync();

        Assert.Equal(1, outcome.Synced);
        Assert.Equal(0, outcome.Pending);
        Assert.Empty(await store.ListPendingAsync());

        var punchBody = _handler.Requests.Single(r => r.Path.EndsWith("clock-in", StringComparison.Ordinal)).Body!;
        Assert.Contains("\"client_punch_id\":\"punch-1\"", punchBody);
        Assert.Contains("\"captured_offline\":true", punchBody);
        Assert.Contains("2026-09-25T08:25:00-04:00", punchBody);
        // The photo upload carries the capture time too, so it lands in the punch day's folder.
        var photoBody = _handler.Requests.Single(r => r.Path.EndsWith("photos", StringComparison.Ordinal)).Body!;
        Assert.Contains("2026-09-25T08:25:00", photoBody);
    }

    [Fact]
    public async Task A_refused_punch_is_parked_and_not_retried()
    {
        var store = Store();
        await store.EnqueueAsync(Punch("punch-1"));
        _handler.On("POST", "/api/clock-kiosk/photos", HttpStatusCode.OK,
            """{"relative_path":"Records/2026-09-25/ClockIN.jpeg","file_name":"ClockIN.jpeg"}""");
        _handler.On("POST", "/api/clock-kiosk/clock-in", HttpStatusCode.BadRequest,
            """{"error":"You are already clocked in. Clock out before starting another shift."}""");
        var worker = new ClockSyncWorker(Api(), store);

        SyncOutcome first = await worker.DrainAsync();
        var callsAfterFirst = _handler.Requests.Count;
        SyncOutcome second = await worker.DrainAsync();

        Assert.Equal(1, first.Parked);
        Assert.Equal(0, second.Parked);
        Assert.Equal(callsAfterFirst, _handler.Requests.Count);
        QueuedPunch parked = Assert.Single(await store.ListParkedAsync());
        Assert.Contains("already clocked in", parked.LastError);
        Assert.Equal(1, await store.CountParkedAsync());
    }

    [Fact]
    public async Task A_second_clock_out_is_still_sent_then_recorded_as_a_duplicate()
    {
        var store = Store();
        var firstOut = new DateTimeOffset(2026, 9, 25, 14, 30, 0, TimeSpan.FromHours(8));
        await store.EnqueueAsync(Punch("out-1", isClockOut: true, at: firstOut));
        await store.EnqueueAsync(Punch("out-2", isClockOut: true, at: firstOut.AddMinutes(1)));
        await store.EnqueueAsync(Punch("in-1", at: firstOut.AddHours(2)));
        await store.EnqueueAsync(Punch("out-3", isClockOut: true, at: firstOut.AddHours(6)));
        _handler.On("POST", "/api/clock-kiosk/photos", HttpStatusCode.OK,
            "{\"relative_path\":\"Records/2026-09-25/ClockOut.jpeg\",\"file_name\":\"ClockOut.jpeg\"}");
        _handler.On("POST", "/api/clock-kiosk/clock-in", HttpStatusCode.OK,
            "{\"action\":\"clock_in\",\"candidate_id\":1042,\"record_id\":78,\"utc_time\":\"2026-09-25T08:30:00Z\"}");
        // The shift is open, closed, then reopened, so the server accepts the first and third clock-outs
        // and refuses the second. The device never decides this on its own: only the server knows.
        _handler.OnSequence(
            "POST",
            "/api/clock-kiosk/clock-out",
            (HttpStatusCode.OK, "{\"action\":\"clock_out\",\"candidate_id\":1042,\"record_id\":77,\"utc_time\":\"2026-09-25T06:30:00Z\"}"),
            (HttpStatusCode.BadRequest, "{\"error\":\"You are not clocked in on any shift.\"}"),
            (HttpStatusCode.OK, "{\"action\":\"clock_out\",\"candidate_id\":1042,\"record_id\":78,\"utc_time\":\"2026-09-25T12:30:00Z\"}"));

        SyncOutcome outcome = await new ClockSyncWorker(Api(), store).DrainAsync();

        Assert.Equal(3, outcome.Synced);
        Assert.Equal(1, outcome.Duplicates);
        Assert.Equal(0, outcome.Pending);
        Assert.Equal(0, outcome.Parked);
        Assert.Empty(await store.ListParkedAsync());
        // The duplicate was offered to the server, so the attempt exists on both sides.
        Assert.Contains(_handler.Requests, r => r.Body != null && r.Body.Contains("out-2", StringComparison.Ordinal));
        Assert.Equal(3, _handler.Requests.Count(r => r.Path.EndsWith("clock-out", StringComparison.Ordinal)));
        Assert.Single(_handler.Requests, r => r.Path.EndsWith("clock-in", StringComparison.Ordinal));
        // The later clock-in and clock-out still went through in order.
        Assert.Equal(
            new[] { "out-1", "out-2", "in-1", "out-3" },
            _handler.Requests
                .Where(r => r.Body != null && r.Body.Contains("client_punch_id", StringComparison.Ordinal))
                .Select(r => r.Body!.Contains("out-1", StringComparison.Ordinal) ? "out-1"
                    : r.Body.Contains("out-2", StringComparison.Ordinal) ? "out-2"
                    : r.Body.Contains("in-1", StringComparison.Ordinal) ? "in-1" : "out-3"));
    }

    [Fact]
    public async Task A_still_offline_drain_keeps_everything_queued_in_order()
    {
        var store = Store();
        var first = new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.FromHours(-4));
        await store.EnqueueAsync(Punch("punch-1", at: first));
        await store.EnqueueAsync(Punch("punch-2", at: first.AddMinutes(5)));
        // No routes registered: the handler answers 404 for photos, which the client treats as a refusal,
        // so instead simulate a dead link.
        _handler.FailWithNetworkError = true;

        SyncOutcome outcome = await new ClockSyncWorker(Api(), store).DrainAsync();

        Assert.False(outcome.Online);
        Assert.Equal(0, outcome.Synced);
        Assert.Equal(2, outcome.Pending);
        List<QueuedPunch> pending = await store.ListPendingAsync();
        Assert.Equal(new[] { "punch-1", "punch-2" }, pending.Select(p => p.ClientPunchId));
    }

    [Fact]
    public async Task A_queued_in_out_out_sequence_syncs_the_pair_and_parks_the_extra_clock_out()
    {
        var store = Store();
        var start = new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.FromHours(-4));
        await store.EnqueueAsync(Punch("in-1", isClockOut: false, at: start));
        await store.EnqueueAsync(Punch("out-1", isClockOut: true, at: start.AddMinutes(90)));
        // A second clock-out for the same shift: the device queued it, the server must refuse it.
        await store.EnqueueAsync(Punch("out-2", isClockOut: true, at: start.AddMinutes(95)));

        _handler.On("POST", "/api/clock-kiosk/photos", HttpStatusCode.OK,
            "{\"relative_path\":\"Records/2026-09-25/ClockIN.jpeg\",\"file_name\":\"ClockIN.jpeg\"}");
        _handler.On("POST", "/api/clock-kiosk/clock-in", HttpStatusCode.OK,
            "{\"action\":\"clock_in\",\"candidate_id\":1042,\"record_id\":77,\"utc_time\":\"2026-09-25T12:00:00Z\"}");
        _handler.OnSequence(
            "POST",
            "/api/clock-kiosk/clock-out",
            (HttpStatusCode.OK, "{\"action\":\"clock_out\",\"candidate_id\":1042,\"record_id\":77,\"hours_worked\":1.5,\"utc_time\":\"2026-09-25T13:30:00Z\"}"),
            (HttpStatusCode.BadRequest, "{\"error\":\"You are not clocked in on any shift.\"}"));


        SyncOutcome outcome = await new ClockSyncWorker(Api(), store).DrainAsync();

        Assert.Equal(2, outcome.Synced);
        Assert.Equal(0, outcome.Pending);
        // The extra clock-out reached the server (so the attempt is on record there) and came back
        // refused. It is a double tap, not a lost punch, so it does not land in the "needs attention" list.
        Assert.Equal(1, outcome.Duplicates);
        Assert.Equal(0, outcome.Parked);
        Assert.Empty(await store.ListParkedAsync());
        Assert.Equal(0, await store.CountParkedAsync());
        // Order held: clock-in first, then the good clock-out, then the refused one.
        Assert.Equal(
            new[] { "/api/clock-kiosk/clock-in", "/api/clock-kiosk/clock-out", "/api/clock-kiosk/clock-out" },
            _handler.Requests
                .Where(r => r.Path.EndsWith("clock-in", StringComparison.Ordinal)
                    || r.Path.EndsWith("clock-out", StringComparison.Ordinal))
                .Select(r => r.Path));
    }

    [Fact]
    public async Task A_restart_during_an_outage_still_knows_who_is_on_shift()
    {
        // Online earlier: the server said Maya is on shift, and that snapshot was persisted.
        var store = Store();
        var clockedInAt = new DateTimeOffset(2026, 9, 25, 7, 36, 0, TimeSpan.FromHours(8));
        await store.ReplaceOpenShiftsAsync(new[] { new KeyValuePair<int, DateTimeOffset>(1042, clockedInAt) });
        await store.ReplaceRosterAsync(
            new[]
            {
                new RosterEntry
                {
                    CardNumber = "04A1C8E291",
                    CandidateId = 1042,
                    FirstName = "Maya",
                    LastName = "Chen",
                    TenantId = 6,
                    OrganizationId = 16,
                    Assignment = "Inbound sort",
                },
            },
            DateTimeOffset.Now);

        // The app restarts with no link: the in-memory cache starts empty.
        await store.DisposeAsync();
        _store = null;
        var reopened = Store();
        var shifts = new KioskShiftCache();
        var service = new HttpClockingService(Api(), shifts, Options(), reopened, new ClockSyncWorker(Api(), reopened));
        await service.PrimeFromCacheAsync();

        _handler.FailWithNetworkError = true;
        var directory = new HttpCandidateBadgeDirectory(Api(), shifts, reopened);
        var badge = await directory.FindByRfidAsync("04A1C8E291");

        Assert.NotNull(badge);
        Assert.Equal("Maya Chen", badge.FullName);
        // The scan is a clock-out, not a second clock-in that the server would file as extra time.
        Assert.Equal(clockedInAt, service.GetOpenClockIn(1042));
        Assert.Equal(1, service.OpenCount);
    }

    [Fact]
    public async Task A_queued_clock_out_beats_the_stale_on_shift_snapshot()
    {
        var store = Store();
        var clockedInAt = new DateTimeOffset(2026, 9, 25, 7, 36, 0, TimeSpan.FromHours(8));
        await store.ReplaceOpenShiftsAsync(new[] { new KeyValuePair<int, DateTimeOffset>(1042, clockedInAt) });
        // They already clocked out on this device during the outage; that punch is still queued.
        await store.EnqueueAsync(Punch("out-1", isClockOut: true, at: clockedInAt.AddHours(8)));

        var shifts = new KioskShiftCache();
        var service = new HttpClockingService(Api(), shifts, Options(), store, new ClockSyncWorker(Api(), store));
        await service.PrimeFromCacheAsync();

        Assert.Null(service.GetOpenClockIn(1042));
        Assert.Equal(0, service.OpenCount);
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public async Task A_punch_waiting_to_sync_still_shows_on_the_live_list()
    {
        var store = Store();
        var tapped = DateTimeOffset.Now.AddMinutes(-20);
        await store.EnqueueAsync(Punch("in-1", isClockOut: false, at: tapped));

        var shifts = new KioskShiftCache();
        var service = new HttpClockingService(Api(), shifts, Options(), store, new ClockSyncWorker(Api(), store));
        await service.PrimeFromCacheAsync();

        ClockEvent shown = Assert.Single(service.GetEvents());
        Assert.Equal(ClockAction.In, shown.Action);
        Assert.True(shown.PendingSync);
        Assert.False(shown.NeedsAttention);
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public async Task A_synced_punch_is_shown_once_by_the_server_not_twice()
    {
        var store = Store();
        var tapped = new DateTimeOffset(2026, 9, 25, 8, 25, 0, TimeSpan.FromHours(8));
        await store.EnqueueAsync(Punch("in-1", isClockOut: false, at: tapped));
        _handler.On("POST", "/api/clock-kiosk/photos", HttpStatusCode.OK,
            "{\"relative_path\":\"Records/2026-09-25/ClockIN.jpeg\",\"file_name\":\"ClockIN.jpeg\"}");
        _handler.On("POST", "/api/clock-kiosk/clock-in", HttpStatusCode.OK,
            "{\"action\":\"clock_in\",\"candidate_id\":1042,\"record_id\":77,\"utc_time\":\"2026-09-25T00:25:00Z\"}");
        _handler.On("GET", "/api/clock-kiosk/roster", HttpStatusCode.OK,
            "{\"candidates\":[],\"server_time\":\"2026-09-25T00:30:00Z\",\"count\":0}");
        _handler.On("GET", "/api/clock-kiosk/sessions", HttpStatusCode.OK,
            "{\"events\":[{\"record_id\":77,\"candidate_id\":1042,\"first_name\":\"Maya\",\"last_name\":\"Chen\"," +
            "\"candidate_name\":\"Maya Chen\",\"action\":\"clock_in\",\"local_time\":\"2026-09-25T08:25:00+08:00\"," +
            "\"assignment\":\"Inbound sort\",\"hours_worked\":null,\"photo_relative_path\":null}]," +
            "\"open_shifts\":[{\"candidate_id\":1042,\"open_clock_in\":\"2026-09-25T08:25:00+08:00\"}],\"open_count\":1}");

        var service = new HttpClockingService(Api(), new KioskShiftCache(), Options(), store, new ClockSyncWorker(Api(), store));
        await service.RefreshAsync();

        ClockEvent shown = Assert.Single(service.GetEvents());
        Assert.False(shown.PendingSync);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task Roster_refresh_caches_candidates_and_measures_clock_skew()
    {
        var store = Store();
        var serverNow = DateTimeOffset.UtcNow.AddSeconds(-30);
        _handler.On("GET", "/api/clock-kiosk/roster", HttpStatusCode.OK, $$"""
            {"candidates":[{"candidate_id":1042,"candidate_number":42,"card_number":"04a1-c8e291","first_name":"Maya",
             "last_name":"Chen","tenant_id":6,"organization_id":16,"assignment":"Inbound sort","client_name":"Sysco"}],
             "server_time":"{{serverNow:o}}","count":1}
            """);

        await new ClockSyncWorker(Api(), store).RefreshRosterAsync();

        RosterEntry? entry = await store.FindRosterByCardAsync("04A1C8E291");
        Assert.NotNull(entry);
        Assert.Equal(1042, entry.CandidateId);
        Assert.Equal("Inbound sort", entry.Assignment);
        Assert.NotNull(await store.GetRosterSyncedAtAsync());
        // Device clock reads ahead of the server by roughly the 30s offset above.
        Assert.InRange(await store.GetClockSkewSecondsAsync(), 25, 35);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new();

        public List<(string Method, string Path, string? Body)> Requests { get; } = new();

        public bool FailWithNetworkError { get; set; }

        private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _sequences = new();

        public void On(string method, string path, HttpStatusCode status, string body) =>
            _routes[$"{method} {path}"] = (status, body);

        /// <summary>Answers the same route differently on each call — a second clock-out is refused.</summary>
        public void OnSequence(string method, string path, params (HttpStatusCode Status, string Body)[] responses) =>
            _sequences[$"{method} {path}"] = new Queue<(HttpStatusCode, string)>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailWithNetworkError)
                throw new HttpRequestException("no route to host");

            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path, body));

            var key = $"{request.Method.Method} {path}";
            if (_sequences.TryGetValue(key, out var queued) && queued.Count > 0)
            {
                var next = queued.Dequeue();
                return new HttpResponseMessage(next.Status)
                {
                    Content = new StringContent(next.Body, Encoding.UTF8, "application/json"),
                };
            }

            if (!_routes.TryGetValue(key, out var route))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
