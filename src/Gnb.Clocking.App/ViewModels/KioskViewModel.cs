using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;
using Gnb.Clocking.Infrastructure.ClockKiosk;

namespace Gnb.Clocking.App.ViewModels;

public enum ServerLink
{
    Connecting,
    Online,
    Offline
}

public sealed class KioskViewModel : INotifyPropertyChanged
{
    private readonly ICandidateBadgeDirectory _directory;
    private readonly IClockingService _clocking;
    private readonly IClock _clock;
    private readonly IClockCamera _camera;
    private readonly IClockPhotoStore _photos;
    private CandidateBadge? _badge;
    private const int ClockInCooldownMinutes = 30;
    private const int ClockOutCooldownMinutes = 15;
    private readonly Dictionary<int, DateTimeOffset> _lastClockOut = new();

    /// <summary>Seconds the cooldown card stays up. Long enough to read the time it names.</summary>
    public const double HoldSeconds = 6;

    private string _holdTitle = "Please wait";
    private string _holdDetail = string.Empty;
    private string _holdSince = string.Empty;
    private string _holdReady = string.Empty;
    private int _returnTicket;
    private bool _started;
    private bool _busy;
    private bool _refreshing;

    private KioskPhase _phase = KioskPhase.Idle;
    private string _badgeText = string.Empty;
    private string _prompt = "Hold a badge to the reader";
    private string _promptDetail = "A scan saves this frame. Times cannot be edited.";
    private string _clockHours = "--";
    private string _clockMinutes = "--";
    private string _clockMeridiem = string.Empty;
    private string _dateLine = string.Empty;
    private bool _colonOn = true;
    private string _candidateName = string.Empty;
    private string _initials = string.Empty;
    private string _candidateIdLine = string.Empty;
    private string _assignmentLine = string.Empty;
    private string _statusPill = string.Empty;
    private string _statusDetail = string.Empty;
    private string _actionTitle = "Clock in";
    private string _actionHint = string.Empty;
    private string _actionError = string.Empty;
    private bool _isOnShift;
    private bool _hasActionError;
    private bool _isBusy;
    private string _successTitle = string.Empty;
    private string _successTime = string.Empty;
    private string _successDetail = string.Empty;
    private string _successPhoto = string.Empty;
    private bool _hasSuccessPhoto;
    private string _unknownMessage = string.Empty;
    private string _sessionCaption = "No punches yet";
    private string _onShiftCaption = "Nobody on shift";
    private string _scopeLine = "GroupNB clock";
    private int _onShiftCount;
    private int _clockInsToday;
    private int _clockOutsToday;
    private ServerLink _server = ServerLink.Connecting;
    private string _serverCaption = "Connecting";
    private string _connectionCaption = "Connecting to the GroupNB clock server";
    private bool _punchesLoaded;
    private int _pendingSyncCount;
    private int _parkedCount;
    private string _offlineCaption = string.Empty;
    private bool _hasOfflineWork;
    private bool _successSavedHere;

    public KioskViewModel(
        ICandidateBadgeDirectory directory,
        IClockingService clocking,
        IClock clock,
        IClockCamera camera,
        IClockPhotoStore photos,
        ClockKioskApiOptions kiosk)
    {
        _directory = directory;
        _clocking = clocking;
        _clock = clock;
        _camera = camera;
        _photos = photos;
        _scopeLine = kiosk.ScopeLine;
        _connectionCaption = kiosk.IsConfigured
            ? "Connecting to the GroupNB clock server…"
            : "The clock server is not configured. Set ClockKiosk__BaseUrl and ClockKiosk__ApiKey in .env.";
        Tick();
        ReloadPunches();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PunchRow> Punches { get; } = new();

    public KioskPhase Phase
    {
        get => _phase;
        private set
        {
            if (!SetProperty(ref _phase, value))
                return;

            Prompt = value switch
            {
                KioskPhase.Reading => "Reading badge",
                KioskPhase.Capturing => "Look at the camera",
                KioskPhase.Hold => _holdTitle,
                _ => "Hold a badge to the reader"
            };
            PromptDetail = value switch
            {
                KioskPhase.Reading => "Matching it to a GroupNB candidate",
                KioskPhase.Capturing => "Hold still. This frame is saved with the punch.",
                KioskPhase.Hold => _holdDetail,
                _ => "A scan saves this frame. Times cannot be edited."
            };
        }
    }

    /// <summary>Cooldown card: the punch that blocks this tap, and when the next tap is allowed.</summary>
    public string HoldTitle { get => _holdTitle; private set => SetProperty(ref _holdTitle, value); }
    public string HoldSince { get => _holdSince; private set => SetProperty(ref _holdSince, value); }
    public string HoldReady { get => _holdReady; private set => SetProperty(ref _holdReady, value); }

    public string BadgeText { get => _badgeText; set => SetProperty(ref _badgeText, value); }
    public string Prompt { get => _prompt; private set => SetProperty(ref _prompt, value); }
    public string PromptDetail { get => _promptDetail; private set => SetProperty(ref _promptDetail, value); }
    public string ClockHours { get => _clockHours; private set => SetProperty(ref _clockHours, value); }
    public string ClockMinutes { get => _clockMinutes; private set => SetProperty(ref _clockMinutes, value); }
    public string ClockMeridiem { get => _clockMeridiem; private set => SetProperty(ref _clockMeridiem, value); }
    public string DateLine { get => _dateLine; private set => SetProperty(ref _dateLine, value); }
    public bool ColonOn { get => _colonOn; private set => SetProperty(ref _colonOn, value); }
    public string CandidateName { get => _candidateName; private set => SetProperty(ref _candidateName, value); }
    public string Initials { get => _initials; private set => SetProperty(ref _initials, value); }
    public string CandidateIdLine { get => _candidateIdLine; private set => SetProperty(ref _candidateIdLine, value); }
    public string AssignmentLine { get => _assignmentLine; private set => SetProperty(ref _assignmentLine, value); }
    public string StatusPill { get => _statusPill; private set => SetProperty(ref _statusPill, value); }
    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }
    public string ActionTitle { get => _actionTitle; private set => SetProperty(ref _actionTitle, value); }
    public string ActionHint { get => _actionHint; private set => SetProperty(ref _actionHint, value); }
    public string ActionError { get => _actionError; private set => SetProperty(ref _actionError, value); }
    public bool IsOnShift { get => _isOnShift; private set => SetProperty(ref _isOnShift, value); }
    public bool HasActionError { get => _hasActionError; private set => SetProperty(ref _hasActionError, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string SuccessTitle { get => _successTitle; private set => SetProperty(ref _successTitle, value); }
    public string SuccessTime { get => _successTime; private set => SetProperty(ref _successTime, value); }
    public string SuccessDetail { get => _successDetail; private set => SetProperty(ref _successDetail, value); }
    public string SuccessPhoto { get => _successPhoto; private set => SetProperty(ref _successPhoto, value); }
    public bool HasSuccessPhoto { get => _hasSuccessPhoto; private set => SetProperty(ref _hasSuccessPhoto, value); }
    public string UnknownMessage { get => _unknownMessage; private set => SetProperty(ref _unknownMessage, value); }
    public string SessionCaption { get => _sessionCaption; private set => SetProperty(ref _sessionCaption, value); }
    public string OnShiftCaption { get => _onShiftCaption; private set => SetProperty(ref _onShiftCaption, value); }
    public string ScopeLine { get => _scopeLine; private set => SetProperty(ref _scopeLine, value); }
    public int OnShiftCount { get => _onShiftCount; private set => SetProperty(ref _onShiftCount, value); }
    public int ClockInsToday { get => _clockInsToday; private set => SetProperty(ref _clockInsToday, value); }
    public int ClockOutsToday { get => _clockOutsToday; private set => SetProperty(ref _clockOutsToday, value); }
    public ServerLink Server { get => _server; private set => SetProperty(ref _server, value); }
    public string ServerCaption { get => _serverCaption; private set => SetProperty(ref _serverCaption, value); }
    public string ConnectionCaption { get => _connectionCaption; private set => SetProperty(ref _connectionCaption, value); }

    /// <summary>Punches sitting on this device, and the ones the server refused when they synced.</summary>
    public int PendingSyncCount { get => _pendingSyncCount; private set => SetProperty(ref _pendingSyncCount, value); }
    public int ParkedCount { get => _parkedCount; private set => SetProperty(ref _parkedCount, value); }
    public string OfflineCaption { get => _offlineCaption; private set => SetProperty(ref _offlineCaption, value); }
    public bool HasOfflineWork { get => _hasOfflineWork; private set => SetProperty(ref _hasOfflineWork, value); }
    /// <summary>This punch is on the device only — the success screen says so rather than implying it synced.</summary>
    public bool SuccessSavedHere { get => _successSavedHere; private set => SetProperty(ref _successSavedHere, value); }

    public void Start()
    {
        if (_started)
            return;

        _started = true;
        Microsoft.Maui.Controls.Application.Current?.Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            Tick();
            return true;
        });

        _ = PrimeThenRefreshAsync();
        Microsoft.Maui.Controls.Application.Current?.Dispatcher.StartTimer(TimeSpan.FromSeconds(30), () =>
        {
            if (!_busy)
                _ = RefreshPunchesAsync();
            return true;
        });
    }

    public async Task SubmitAsync()
    {
        var rfid = RfidNormalizer.Normalize(BadgeText);
        BadgeText = string.Empty;
        if (rfid.Length < 8)
            return;

        if (_busy)
            return;

        _returnTicket++;
        _busy = true;
        IsBusy = true;
        ClearError();
        Phase = KioskPhase.Reading;

        try
        {
            await Task.Delay(460);
            var badge = await _directory.FindByRfidAsync(rfid);
            if (badge == null)
            {
                UnknownMessage = $"No GroupNB candidate is linked to {rfid}.";
                _badge = null;
                Phase = KioskPhase.Unknown;
                return;
            }

            Show(badge);
            var openAt = _clocking.GetOpenClockIn(badge.CandidateId);
            if (IsOnShift && openAt is DateTimeOffset started && Now() - started < TimeSpan.FromMinutes(ClockInCooldownMinutes))
            {
                var ready = started.ToLocalTime().AddMinutes(ClockInCooldownMinutes);
                ShowHold(
                    "You're already clocked in",
                    $"Clocked in at {started.ToLocalTime():h:mm tt}",
                    $"You can clock out after {ready:h:mm tt}");
                return;
            }

            if (LatestClockOut(badge) is DateTimeOffset ended
                && Now() - ended < TimeSpan.FromMinutes(ClockOutCooldownMinutes)
                && (openAt is null || openAt.Value <= ended))
            {
                var ready = ended.ToLocalTime().AddMinutes(ClockOutCooldownMinutes);
                ShowHold(
                    "You're already clocked out",
                    $"Clocked out at {ended.ToLocalTime():h:mm tt}",
                    $"You can tap again after {ready:h:mm tt}");
                return;
            }

            if (!IsOnShift && badge.ShiftFinished)
            {
                await CaptureAndPunchAsync(badge, resetCompletedDay: true);
                return;
            }

            await CaptureAndPunchAsync(badge, resetCompletedDay: false);
        }
        catch (ClockingException ex)
        {
            UnknownMessage = ex.Message;
            Phase = KioskPhase.Unknown;
        }
        finally
        {
            _busy = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Clock-out that still blocks another tap. A punchcard edit that clears the finished shift wins,
    /// so the next tap is sent. Right after a punch, the device time covers a badge that has not caught up.
    /// </summary>
    private DateTimeOffset? LatestClockOut(CandidateBadge badge)
    {
        if (!IsOnShift && !badge.ShiftFinished)
        {
            _lastClockOut.Remove(badge.CandidateId);
            return null;
        }

        DateTimeOffset? latest = badge.ShiftFinished ? badge.FinishedClockOut : null;
        if (_lastClockOut.TryGetValue(badge.CandidateId, out DateTimeOffset local) && (latest is null || local > latest))
            latest = local;

        return latest;
    }

    private void ShowHold(string title, string since, string ready)
    {
        HoldTitle = title;
        HoldSince = since;
        HoldReady = ready;
        _holdDetail = $"{since}. {ready}.";
        Phase = KioskPhase.Hold;
        var ticket = ++_returnTicket;
        _ = ReturnToIdleAsync(ticket, TimeSpan.FromSeconds(HoldSeconds), KioskPhase.Hold);
    }

    private async Task CaptureAndPunchAsync(CandidateBadge badge, bool resetCompletedDay)
    {
        Phase = KioskPhase.Capturing;
        await Task.Delay(700);
        var jpeg = await _camera.CaptureJpegAsync();
        if (jpeg == null || jpeg.Length == 0)
        {
            UnknownMessage = "A photo is required to clock in or out.";
            Phase = KioskPhase.Unknown;
            return;
        }

        var openAt = _clocking.GetOpenClockIn(badge.CandidateId);
        var referenceDate = IsOnShift && openAt is DateTimeOffset started
            ? started.LocalDateTime.Date
            : Now().DateTime.Date;
        var photo = await _photos.SaveJpegAsync(badge, IsOnShift, referenceDate, jpeg);
        var clockEvent = IsOnShift
            ? await _clocking.ClockOutAsync(badge, photo)
            : await _clocking.ClockInAsync(badge, photo, resetCompletedDay);

        SuccessSavedHere = clockEvent.PendingSync;
        if (clockEvent.PendingSync)
            UpdateOfflineCounters();
        // Say what was actually saved. The server decides in or out, and whether it landed on extra
        // time, so the screen follows its answer rather than the action this kiosk asked for.
        var savedIn = clockEvent.Action == ClockAction.In;
        if (!savedIn)
            _lastClockOut[badge.CandidateId] = clockEvent.At;
        SuccessTitle = clockEvent.IsExtra || resetCompletedDay
            ? savedIn ? "Extra time started" : "Extra time ended"
            : savedIn ? "Clocked in" : "Clocked out";
        SuccessTime = clockEvent.At.ToLocalTime().ToString("h:mm tt");
        SuccessDetail = clockEvent.IsExtra || resetCompletedDay
            ? $"{clockEvent.CandidateName} · today's saved times stay. This is not added to hours."
            : !savedIn && clockEvent.HoursWorked is double hours
                ? $"{clockEvent.CandidateName} · {hours:0.##}h on shift"
                : $"{clockEvent.CandidateName} · {clockEvent.Assignment}";
        SuccessPhoto = photo.AbsolutePath;
        HasSuccessPhoto = true;

        MarkOnline();
        ReloadPunches();
        Phase = KioskPhase.Success;
        var ticket = ++_returnTicket;
        _ = ReturnToIdleAsync(ticket, TimeSpan.FromSeconds(3.6), KioskPhase.Success);
    }

    public void Dismiss()
    {
        _returnTicket++;
        _badge = null;
        UnknownMessage = string.Empty;
        SuccessPhoto = string.Empty;
        HasSuccessPhoto = false;
        ClearError();
        Phase = KioskPhase.Idle;
    }

    private void Show(CandidateBadge badge)
    {
        _badge = badge;
        var openAt = _clocking.GetOpenClockIn(badge.CandidateId);
        IsOnShift = openAt.HasValue;
        CandidateName = badge.FullName;
        Initials = badge.Initials;
        CandidateIdLine = string.IsNullOrWhiteSpace(badge.CardNumber)
            ? string.Empty
            : $"Card {badge.CardNumber}";
        AssignmentLine = $"{badge.Assignment}  ·  {badge.ClientName}  ·  {badge.Site}";
        StatusPill = IsOnShift ? "On shift" : badge.ShiftFinished ? "Finished today" : "Off shift";
        StatusDetail = openAt is DateTimeOffset start
            ? $"Since {start.ToLocalTime():h:mm tt}  ·  {Format(Now() - start)} so far"
            : badge.ShiftFinished
                ? "Today's times stay. The next tap starts extra time."
                : "Ready to start a shift";
        ActionTitle = IsOnShift ? "Clock out" : badge.ShiftFinished ? "Extra time" : "Clock in";
        ActionHint = IsOnShift
            ? "Closes the open shift"
            : badge.ShiftFinished
                ? "Starts extra time. Today's clock in and clock out stay."
                : $"Starts {badge.FirstName}'s shift";
    }

    private async Task ReturnToIdleAsync(int ticket, TimeSpan delay, KioskPhase phase)
    {
        await Task.Delay(delay);
        if (ticket != _returnTicket || Phase != phase)
            return;

        Dismiss();
    }

    private void Tick()
    {
        var now = Now();
        var hour = now.Hour % 12;
        if (hour == 0)
            hour = 12;
        ClockHours = hour.ToString();
        ClockMinutes = now.Minute.ToString("00");
        ClockMeridiem = now.ToString("tt").ToUpperInvariant();
        DateLine = now.ToString("dddd, MMM d");
        ColonOn = !ColonOn;

        if (Phase == KioskPhase.Recognized && _badge != null && _clocking.GetOpenClockIn(_badge.CandidateId) is DateTimeOffset start)
            StatusDetail = $"Since {start.ToLocalTime():h:mm tt}  ·  {Format(now - start)} so far";
    }

    /// <summary>Device state first (it needs no link), then the server.</summary>
    private async Task PrimeThenRefreshAsync()
    {
        try
        {
            await _clocking.PrimeFromCacheAsync();
            UpdateOfflineCounters();
            ReloadPunches();
        }
        catch (ClockingException)
        {
            // Nothing cached yet; the refresh below will fill it in when the link is up.
        }

        await RefreshPunchesAsync();
    }

    private async Task RefreshPunchesAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            await _clocking.RefreshAsync();
            UpdateOfflineCounters();
            MarkOnline();
        }
        catch (ClockingException ex)
        {
            Server = ServerLink.Offline;
            ServerCaption = "Offline";
            UpdateOfflineCounters();
            ConnectionCaption = PendingSyncCount > 0
                ? $"Offline — {BuildOfflineCaption(PendingSyncCount, ParkedCount)}. Scans still work."
                : $"{ex.Message} Scans still work and are saved on this device.";
        }
        finally
        {
            _refreshing = false;
        }

        ReloadPunches();
    }

    private void UpdateOfflineCounters()
    {
        PendingSyncCount = _clocking.PendingCount;
        ParkedCount = _clocking.ParkedCount;
        HasOfflineWork = PendingSyncCount > 0 || ParkedCount > 0;
        OfflineCaption = BuildOfflineCaption(PendingSyncCount, ParkedCount);
    }

    private static string BuildOfflineCaption(int pending, int parked)
    {
        if (parked > 0 && pending > 0)
            return $"{PunchCountLabel(pending)} waiting to sync  ·  {parked} need attention";
        if (parked > 0)
            return parked == 1 ? "1 punch needs attention" : $"{parked} punches need attention";
        if (pending > 0)
            return $"{PunchCountLabel(pending)} saved here, waiting to sync";
        return string.Empty;
    }

    private static string PunchCountLabel(int count) => count == 1 ? "1 punch" : $"{count} punches";

    private void MarkOnline()
    {
        Server = ServerLink.Online;
        ServerCaption = PendingSyncCount > 0 ? "Syncing" : "Server synced";
        ConnectionCaption = $"Synced with the GroupNB clock server at {Now():h:mm tt}";
    }

    private void ReloadPunches()
    {
        var events = _clocking.GetEvents();
        var today = Now().Date;
        var rows = events
            .Where(clockEvent => ShowOnLiveList(clockEvent, today))
            .OrderByDescending(clockEvent => clockEvent.At)
            .Select(PunchRow.From)
            .ToArray();

        MergePunches(rows);

        var count = rows.Length;
        SessionCaption = count == 0 ? "No punches yet today" : count == 1 ? "1 event today" : $"{count} events today";
        var open = _clocking.OpenCount;
        OnShiftCaption = open == 0 ? "Nobody on shift" : open == 1 ? "1 on shift" : $"{open} on shift";
        OnShiftCount = open;

        ClockInsToday = events.Count(e => e.Action == ClockAction.In && e.At.ToLocalTime().Date == today);
        ClockOutsToday = events.Count(e => e.Action == ClockAction.Out && e.At.ToLocalTime().Date == today);
    }

    /// <summary>Today's punches, plus a clock-in that still has no clock-out.</summary>
    private bool ShowOnLiveList(ClockEvent clockEvent, DateTime today)
    {
        if (clockEvent.At.ToLocalTime().Date == today)
            return true;

        return clockEvent.Action == ClockAction.In
            && _clocking.GetOpenClockIn(clockEvent.CandidateId) is DateTimeOffset open
            && open.UtcDateTime == clockEvent.At.UtcDateTime;
    }

    /// <summary>
    /// Inserts only the new punches at the top so the list animates arrivals instead of
    /// rebuilding every refresh. Falls back to a full reset when the order changed.
    /// </summary>
    private void MergePunches(IReadOnlyList<PunchRow> rows)
    {
        var animate = _punchesLoaded;
        _punchesLoaded = true;

        var existing = Punches.Select(row => row.Key).ToHashSet();
        var added = rows.TakeWhile(row => !existing.Contains(row.Key)).ToList();
        var kept = rows.Skip(added.Count).Select(row => row.Key).ToList();
        var matches = kept.Count <= Punches.Count
            && kept.SequenceEqual(Punches.Take(kept.Count).Select(row => row.Key));

        if (!matches)
        {
            Punches.Clear();
            foreach (var row in rows)
                Punches.Add(row);
            return;
        }

        while (Punches.Count > kept.Count)
            Punches.RemoveAt(Punches.Count - 1);

        for (var i = added.Count - 1; i >= 0; i--)
        {
            added[i].IsFresh = animate;
            Punches.Insert(0, added[i]);
        }
    }

    private void ClearError()
    {
        ActionError = string.Empty;
        HasActionError = false;
    }

    private DateTimeOffset Now() => _clock.Now.ToLocalTime();

    private static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        var hours = (int)span.TotalHours;
        return hours >= 1 ? $"{hours}h {span.Minutes}m" : $"{Math.Max(span.Minutes, 0)}m";
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
