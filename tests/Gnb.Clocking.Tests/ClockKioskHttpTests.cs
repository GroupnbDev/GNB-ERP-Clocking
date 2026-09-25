using System.Net;
using System.Text;
using System.Text.Json;
using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;
using Gnb.Clocking.Infrastructure.ClockKiosk;
using Gnb.Clocking.Infrastructure.ClockKiosk.Offline;
using Gnb.Clocking.Infrastructure.Photos;
using Xunit;

namespace Gnb.Clocking.Tests;

public sealed class ClockKioskHttpTests : IDisposable
{
    private const string Key = "test-kiosk-key-0123456789abcdef0123";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnb-clock-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _handler = new();
    private readonly KioskShiftCache _shifts = new();

    private static readonly CandidateBadge Maya = new(
        1042, 42, "Maya", "Chen", "04A1C8E291", "Candidate", "Inbound sort", "Sysco", "Brampton DC", 6, 16, "04A1C8E291");

    public void Dispose()
    {
        _store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ClockKioskApiOptions Options(string? key = Key) => new()
    {
        BaseUrl = "http://clock.test",
        ApiKey = key,
        PhotoRoot = _root,
    };

    private ClockKioskApiClient Api(string? key = Key) => new(new HttpClient(_handler), Options(key));

    private OfflineClockStore? _store;

    private OfflineClockStore Store()
    {
        Directory.CreateDirectory(_root);
        return _store ??= new OfflineClockStore(Path.Combine(_root, "clock-queue.db3"));
    }

    private ClockSyncWorker Sync(string? key = Key) => new(Api(key), Store());

    [Fact]
    public async Task Badge_lookup_resolves_the_candidate_and_remembers_the_open_shift()
    {
        _handler.On("GET", "/api/clock-kiosk/badges/04A1C8E291", HttpStatusCode.OK, """
            {"candidate_id":1042,"candidate_number":42,"card_number":"04A1C8E291","first_name":"Maya","last_name":"Chen","rfid":"04A1C8E291",
             "tenant_id":6,"organization_id":16,"is_on_shift":true,"open_clock_in":"2026-09-22T08:00:00-04:00",
             "reference_date":"2026-09-22","assignment":"Inbound sort","client_name":"Sysco","site":"Brampton DC","punch_card_id":500}
            """);
        var directory = new HttpCandidateBadgeDirectory(Api(), _shifts, Store());

        var badge = await directory.FindByRfidAsync(";04a1c8e291?");

        Assert.NotNull(badge);
        Assert.Equal(1042, badge.CandidateId);
        Assert.Equal("Maya Chen", badge.FullName);
        Assert.Equal("04A1C8E291", badge.CardNumber);
        Assert.Equal("Brampton DC", badge.Site);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.FromHours(-4)), _shifts.GetOpenClockIn(1042));
        Assert.Equal(Key, _handler.Requests.Single().Key);
    }

    [Fact]
    public async Task Unknown_or_out_of_scope_badge_is_null()
    {
        _handler.On("GET", "/api/clock-kiosk/badges/04DEAD0001", HttpStatusCode.NotFound, """{"error":"nope"}""");
        var directory = new HttpCandidateBadgeDirectory(Api(), _shifts, Store());

        Assert.Null(await directory.FindByRfidAsync("04DEAD0001"));
        Assert.Empty(directory.ListLinkedBadges());
    }

    [Fact]
    public async Task Clock_in_posts_rfid_and_image_path_then_refreshes_sessions()
    {
        _handler.On("POST", "/api/clock-kiosk/clock-in", HttpStatusCode.OK, """
            {"action":"clock_in","candidate_id":1042,"record_id":77,"utc_time":"2026-09-22T12:00:00Z",
             "demand_name":"Inbound sort","image_path":"Records/2026-09-22/ClockIN.jpeg"}
            """);
        _handler.On("GET", "/api/clock-kiosk/sessions", HttpStatusCode.OK, """
            {"events":[{"record_id":77,"candidate_id":1042,"first_name":"Maya","last_name":"Chen","candidate_name":"Maya Chen",
             "action":"clock_in","local_time":"2026-09-22T08:00:00-04:00","assignment":"Inbound sort","hours_worked":null,
             "photo_relative_path":"Records/2026-09-22/ClockIN.jpeg"}],
             "open_shifts":[{"candidate_id":1042,"open_clock_in":"2026-09-22T08:00:00-04:00"}],"open_count":1}
            """);
        var service = new HttpClockingService(Api(), _shifts, Options(), Store(), Sync());

        var clockEvent = await service.ClockInAsync(Maya, Photo(isClockOut: false));

        Assert.Equal(ClockAction.In, clockEvent.Action);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero), clockEvent.At);
        var body = JsonDocument.Parse(_handler.Requests[0].Body!).RootElement;
        Assert.Equal("04A1C8E291", body.GetProperty("rfid").GetString());
        Assert.Equal("Records/2026-09-22/ClockIN.jpeg", body.GetProperty("image_path").GetString());
        Assert.Equal(1, service.OpenCount);
        Assert.Single(service.GetEvents());
        Assert.NotNull(service.GetOpenClockIn(1042));
    }

    [Fact]
    public async Task Clock_out_returns_hours_from_the_server()
    {
        _handler.On("POST", "/api/clock-kiosk/clock-out", HttpStatusCode.OK, """
            {"action":"clock_out","candidate_id":1042,"record_id":77,"hours_worked":1.5,"utc_time":"2026-09-22T13:30:00Z",
             "demand_name":"Inbound sort","image_path":"Records/2026-09-22/ClockOut.jpeg"}
            """);
        _handler.On("GET", "/api/clock-kiosk/sessions", HttpStatusCode.OK, """{"events":[],"open_shifts":[],"open_count":0}""");
        _shifts.SetShift(1042, DateTimeOffset.UtcNow);
        var service = new HttpClockingService(Api(), _shifts, Options(), Store(), Sync());

        var clockEvent = await service.ClockOutAsync(Maya, Photo(isClockOut: true));

        Assert.Equal(ClockAction.Out, clockEvent.Action);
        Assert.Equal(1.5, clockEvent.HoursWorked);
        Assert.Null(service.GetOpenClockIn(1042));
    }

    [Fact]
    public async Task The_server_decides_whether_a_punch_was_in_or_out()
    {
        // The kiosk thinks this is a clock-out; the server says the shift was already closed and
        // records a clock-in. The screen must report the clock-in, not the kiosk's guess.
        _handler.On("POST", "/api/clock-kiosk/clock-out", HttpStatusCode.OK,
            """
            {"action":"clock_in","candidate_id":1042,"record_id":77,"utc_time":"2026-09-25T12:00:00Z",
             "demand_name":"Inbound sort","image_path":"Records/2026-09-25/ClockIN.jpeg","is_extra":false}
            """);
        _handler.On("GET", "/api/clock-kiosk/sessions", HttpStatusCode.OK, """{"events":[],"open_shifts":[],"open_count":0}""");
        _handler.On("GET", "/api/clock-kiosk/roster", HttpStatusCode.OK, """{"candidates":[],"server_time":"2026-09-25T12:00:00Z","count":0}""");
        var service = new HttpClockingService(Api(), _shifts, Options(), Store(), Sync());

        var clockEvent = await service.ClockOutAsync(Maya, Photo(isClockOut: true));

        Assert.Equal(ClockAction.In, clockEvent.Action);
        Assert.False(clockEvent.IsExtra);
        Assert.Null(clockEvent.HoursWorked);
    }

    [Fact]
    public async Task A_punch_the_server_filed_as_extra_time_says_so_and_leaves_the_shift_alone()
    {
        _shifts.SetShift(1042, DateTimeOffset.Now.AddHours(-3));
        _handler.On("POST", "/api/clock-kiosk/clock-in", HttpStatusCode.OK,
            """
            {"action":"clock_in","candidate_id":1042,"record_id":77,"utc_time":"2026-09-25T12:00:00Z",
             "demand_name":"Inbound sort","image_path":"Records/2026-09-25/ClockIN.jpeg","is_extra":true}
            """);
        _handler.On("GET", "/api/clock-kiosk/sessions", HttpStatusCode.OK, """{"events":[],"open_shifts":[],"open_count":0}""");
        _handler.On("GET", "/api/clock-kiosk/roster", HttpStatusCode.OK, """{"candidates":[],"server_time":"2026-09-25T12:00:00Z","count":0}""");
        var service = new HttpClockingService(Api(), _shifts, Options(), Store(), Sync());

        var clockEvent = await service.ClockInAsync(Maya, Photo(isClockOut: false), resetCompletedDay: false);

        Assert.True(clockEvent.IsExtra);
        Assert.Equal(ClockAction.In, clockEvent.Action);
    }

    [Fact]
    public async Task Server_400_message_surfaces_as_ClockingException()
    {
        _handler.On("POST", "/api/clock-kiosk/clock-in", HttpStatusCode.BadRequest,
            """{"error":"You are already clocked in. Clock out before starting another shift."}""");
        var service = new HttpClockingService(Api(), _shifts, Options(), Store(), Sync());

        var error = await Assert.ThrowsAsync<ClockingException>(() => service.ClockInAsync(Maya, Photo(isClockOut: false)));

        Assert.Equal("You are already clocked in. Clock out before starting another shift.", error.Message);
    }

    [Fact]
    public async Task Punch_without_a_photo_never_reaches_the_server()
    {
        var service = new HttpClockingService(Api(), _shifts, Options(), Store(), Sync());

        var error = await Assert.ThrowsAsync<ClockingException>(() => service.ClockInAsync(Maya, new ClockPhoto("", "")));

        Assert.Equal("A photo is required to clock in or out.", error.Message);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Missing_kiosk_key_is_a_configuration_error()
    {
        var service = new HttpClockingService(Api(key: null), _shifts, Options(key: null), Store(), Sync(key: null));

        var error = await Assert.ThrowsAsync<ClockOfflineException>(() => service.RefreshAsync());

        Assert.Contains("ClockKiosk:ApiKey", error.Message);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Photo_store_uploads_then_moves_the_local_copy_to_the_server_day()
    {
        // Overnight: the kiosk thinks today, the server files the clock-out under the open shift's day.
        _handler.On("POST", "/api/clock-kiosk/photos", HttpStatusCode.OK,
            """{"relative_path":"Records/2026-09-21/ClockOut.jpeg","file_name":"ClockOut.jpeg"}""");
        var store = new HttpClockPhotoStore(new FileClockPhotoStore(_root), Api(), Options());

        var photo = await store.SaveJpegAsync(Maya, isClockOut: true, new DateTime(2026, 9, 22), new byte[] { 0xFF, 0xD8, 0xFF });

        Assert.Equal("Records/2026-09-21/ClockOut.jpeg", photo.RelativePath);
        Assert.True(File.Exists(photo.AbsolutePath));
        Assert.EndsWith(Path.Combine("Candidates", "1042", "Records", "2026-09-21", "ClockOut.jpeg"), photo.AbsolutePath);
        Assert.False(File.Exists(Path.Combine(_root, "Candidates", "1042", "Records", "2026-09-22", "ClockOut.jpeg")));
        var upload = _handler.Requests.Single().Body!;
        Assert.Contains("name=rfid", upload);
        Assert.Contains("04A1C8E291", upload);
    }

    [Fact]
    public async Task File_store_saves_clock_in_jpeg_under_the_candidate_record_day()
    {
        var photo = await new FileClockPhotoStore(_root).SaveJpegAsync(Maya, false, new DateTime(2026, 9, 22), new byte[] { 1, 2, 3 });

        Assert.Equal("Records/2026-09-22/ClockIN.jpeg", photo.RelativePath);
        Assert.True(File.Exists(photo.AbsolutePath));
    }

    private ClockPhoto Photo(bool isClockOut) =>
        new(ClockPhotoPaths.RelativePath(new DateTime(2026, 9, 22), isClockOut), Path.Combine(_root, "clock.jpg"));

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new();

        public List<(string Method, string Path, string? Key, string? Body)> Requests { get; } = new();

        public void On(string method, string path, HttpStatusCode status, string body) =>
            _routes[$"{method} {path}"] = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var key = request.Headers.TryGetValues(ClockKioskApiOptions.KeyHeaderName, out var values) ? values.Single() : null;
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path, key, body));

            if (!_routes.TryGetValue($"{request.Method.Method} {path}", out var route))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
