using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Gnb.Clocking.App.Configuration;
using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;
using Gnb.Clocking.Infrastructure.ClockKiosk;
using Microsoft.Extensions.Logging;

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
    private readonly Dictionary<int, DateTimeOffset> _lastClockOut = new();

    /// <summary>Seconds the cooldown card stays up. Long enough to read the time it names.</summary>
    public const double HoldSeconds = 6;

    /// <summary>Seconds the RFID dialog stays up. Long enough to write the number down; the next tap closes it.</summary>
    public const double RfidDialogSeconds = 10;

    private string _holdTitle = "Please wait";
    private string _holdDetail = string.Empty;
    private string _holdSince = string.Empty;
    private string _holdReady = string.Empty;
    private string _rfidNumber = string.Empty;
    private string _rfidTitle = string.Empty;
    private string _rfidDetail = string.Empty;
    private string _rfidHolderName = string.Empty;
    private string _rfidHolderStatus = string.Empty;
    private bool _hasRfidHolder;
    private bool _hasRfidHolderStatus;
    private bool _isReassign;
    private int _returnTicket;
    private readonly ILogger<KioskViewModel> _logger;
    private readonly CameraSettings _cameraSettings;
    private readonly ResetCameraCommand _resetCamera;
    private DateTime _lastTapUtc = DateTime.MinValue;
    private DateTime? _lastDailyRebuild;
    private int _dailyWaits;
    private bool _isResettingCamera;
    private bool _resetCoolingDown;
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
    private string _assignmentLine = string.Empty;
    private string _statusDetail = string.Empty;
    private string _actionTitle = "Clock in";
    private bool _isOnShift;
    private bool _isBusy;
    private string _successTitle = string.Empty;
    private string _successTime = string.Empty;
    private string _successDetail = string.Empty;
    private string _successPhoto = string.Empty;
    private bool _hasSuccessPhoto;
    private string _unknownMessage = string.Empty;
    private string _sessionCaption = "No punches yet";
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
        ClockKioskApiOptions kiosk,
        CameraSettings cameraSettings,
        ILogger<KioskViewModel> logger)
    {
        _directory = directory;
        _clocking = clocking;
        _clock = clock;
        _camera = camera;
        _photos = photos;
        _cameraSettings = cameraSettings;
        _logger = logger;
        _resetCamera = new ResetCameraCommand(this);
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
                KioskPhase.Unassigned => _rfidTitle,
                _ => "Hold a badge to the reader"
            };
            PromptDetail = value switch
            {
                KioskPhase.Reading => "Matching it to a GroupNB candidate",
                KioskPhase.Capturing => "Hold still. This frame is saved with the punch.",
                KioskPhase.Hold => _holdDetail,
                KioskPhase.Unassigned => $"RFID {_rfidNumber}",
                _ => "A scan saves this frame. Times cannot be edited."
            };
        }
    }

    /// <summary>Cooldown card: the punch that blocks this tap, and when the next tap is allowed.</summary>
    public string HoldTitle { get => _holdTitle; private set => SetProperty(ref _holdTitle, value); }
    public string HoldSince { get => _holdSince; private set => SetProperty(ref _holdSince, value); }
    public string HoldReady { get => _holdReady; private set => SetProperty(ref _holdReady, value); }

    /// <summary>RFID dialog: the card number staff assign (unknown card) or reassign (candidate not Working).</summary>
    public string RfidNumber { get => _rfidNumber; private set => SetProperty(ref _rfidNumber, value); }
    public string RfidTitle { get => _rfidTitle; private set => SetProperty(ref _rfidTitle, value); }
    public string RfidDetail { get => _rfidDetail; private set => SetProperty(ref _rfidDetail, value); }
    /// <summary>Who holds the card now, and the status that stops them clocking (reassign only).</summary>
    public string RfidHolderName { get => _rfidHolderName; private set => SetProperty(ref _rfidHolderName, value); }
    public string RfidHolderStatus { get => _rfidHolderStatus; private set => SetProperty(ref _rfidHolderStatus, value); }
    public bool HasRfidHolder { get => _hasRfidHolder; private set => SetProperty(ref _hasRfidHolder, value); }
    public bool HasRfidHolderStatus { get => _hasRfidHolderStatus; private set => SetProperty(ref _hasRfidHolderStatus, value); }
    public bool IsReassign { get => _isReassign; private set => SetProperty(ref _isReassign, value); }

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
    public string AssignmentLine { get => _assignmentLine; private set => SetProperty(ref _assignmentLine, value); }
    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }
    public string ActionTitle { get => _actionTitle; private set => SetProperty(ref _actionTitle, value); }
    public bool IsOnShift { get => _isOnShift; private set => SetProperty(ref _isOnShift, value); }
    public bool IsBusy { get => _isBusy; private set => SetFlag(ref _isBusy, value); }
    public bool IsResettingCamera { get => _isResettingCamera; private set => SetFlag(ref _isResettingCamera, value); }
    public bool CanResetCamera => !_busy && !_isResettingCamera && !_resetCoolingDown;
    public string ResetCameraText => IsResettingCamera ? "Resetting…" : "Reset camera";
    public ICommand ResetCameraCommand => _resetCamera;

    /// <summary>The page re-claims the badge field. Camera work must not keep the keyboard.</summary>
    public event Action? FocusBadgeRequested;
    public string SuccessTitle { get => _successTitle; private set => SetProperty(ref _successTitle, value); }
    public string SuccessTime { get => _successTime; private set => SetProperty(ref _successTime, value); }
    public string SuccessDetail { get => _successDetail; private set => SetProperty(ref _successDetail, value); }
    public string SuccessPhoto { get => _successPhoto; private set => SetProperty(ref _successPhoto, value); }
    public bool HasSuccessPhoto { get => _hasSuccessPhoto; private set => SetProperty(ref _hasSuccessPhoto, value); }
    public string UnknownMessage { get => _unknownMessage; private set => SetProperty(ref _unknownMessage, value); }
    public string SessionCaption { get => _sessionCaption; private set => SetProperty(ref _sessionCaption, value); }
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
        Microsoft.Maui.Controls.Application.Current?.Dispatcher.StartTimer(TimeSpan.FromSeconds(60), () =>
        {
            _ = MaybeDailyRebuildAsync();
            return true;
        });
    }

    public async Task SubmitAsync()
    {
        var started = Stopwatch.GetTimestamp();
        var rfid = RfidNormalizer.Normalize(BadgeText);
        BadgeText = string.Empty;
        Exception? failure = null;
        if (rfid.Length < 8)
            return;

        if (_busy)
            return;

        _lastTapUtc = DateTime.UtcNow;
        if (_camera.Health != CameraHealth.Live)
        {
            UnknownMessage = "The camera is reconnecting. Hold the badge to the reader again in a few seconds.";
            Phase = KioskPhase.Unknown;
            LogTap(started, rfid, null);
            var ticket = ++_returnTicket;
            _ = ReturnToIdleAsync(ticket, TimeSpan.FromSeconds(4), KioskPhase.Unknown);
            return;
        }

        _returnTicket++;
        _busy = true;
        IsBusy = true;
        Phase = KioskPhase.Reading;

        try
        {
            await Task.Delay(460);
            using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var badge = await _directory.FindByRfidAsync(rfid, lookup.Token);
            if (badge == null)
            {
                ShowRfidDialog(rfid, reassign: false, holderName: string.Empty, holderStatus: string.Empty);
                return;
            }

            Show(badge);
            var openAt = IsOnShift ? _clocking.GetOpenClockIn(badge.CandidateId) : null;
            switch (ClockCooldownPolicy.Evaluate(Now(), openAt, LatestClockOut(badge)))
            {
                case { Kind: ClockCooldownKind.AfterClockIn } holdIn:
                    ShowHold(
                        "You're already clocked in",
                        $"Clocked in at {holdIn.PunchedAt.ToLocalTime():h:mm tt}",
                        $"You can clock out after {holdIn.ReadyAt.ToLocalTime():h:mm tt}");
                    return;

                case { Kind: ClockCooldownKind.AfterClockOut } holdOut:
                    ShowHold(
                        "You're already clocked out",
                        $"Clocked out at {holdOut.PunchedAt.ToLocalTime():h:mm tt}",
                        $"You can tap again after {holdOut.ReadyAt.ToLocalTime():h:mm tt}");
                    return;
            }

            if (!IsOnShift && badge.ShiftFinished)
            {
                await CaptureAndPunchAsync(badge, resetCompletedDay: true);
                return;
            }

            await CaptureAndPunchAsync(badge, resetCompletedDay: false);
        }
        catch (BadgeNotWorkingException ex)
        {
            failure = ex;
            ShowRfidDialog(
                string.IsNullOrWhiteSpace(ex.Rfid) ? rfid : ex.Rfid,
                reassign: true,
                ex.CandidateName.Trim(),
                ex.Status?.Trim() ?? string.Empty);
        }
        catch (ClockingException ex)
        {
            failure = ex;
            UnknownMessage = ex.Message;
            Phase = KioskPhase.Unknown;
            var ticket = ++_returnTicket;
            _ = ReturnToIdleAsync(ticket, TimeSpan.FromSeconds(4), KioskPhase.Unknown);
        }
        catch (OperationCanceledException ex)
        {
            failure = ex;
            UnknownMessage = "This tap did not finish. Hold the badge to the reader again.";
            Phase = KioskPhase.Unknown;
            var ticket = ++_returnTicket;
            _ = ReturnToIdleAsync(ticket, TimeSpan.FromSeconds(4), KioskPhase.Unknown);
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogError(ex, "Tap failed");
            UnknownMessage = "Something went wrong with this tap. Hold the badge to the reader again.";
            Phase = KioskPhase.Unknown;
            var ticket = ++_returnTicket;
            _ = ReturnToIdleAsync(ticket, TimeSpan.FromSeconds(4), KioskPhase.Unknown);
        }
        finally
        {
            _busy = false;
            IsBusy = false;
            LogTap(started, rfid, failure);
        }
    }

    public async Task ResetCameraAsync()
    {
        if (_busy || _isResettingCamera || _resetCoolingDown)
            return;

        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation("Camera reset started trigger manual");
        Dismiss();
        FocusBadgeRequested?.Invoke();
        IsResettingCamera = true;
        try
        {
            // The rebuild returns once a session is opened or given up on; only a frame proves the camera is back.
            var budget = TimeSpan.FromSeconds(30);
            await _camera.RebuildAsync("manual").WaitAsync(budget);
            var remaining = budget - Stopwatch.GetElapsedTime(started);
            if (!await WaitForLiveAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
                throw new ClockingException($"The camera is still {_camera.Health} after the reset.");

            _logger.LogInformation(
                "Camera reset finished trigger manual elapsed {ElapsedMs} outcome ok",
                ElapsedMs(started));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Camera reset finished trigger manual elapsed {ElapsedMs} outcome failed",
                ElapsedMs(started));
            UnknownMessage = "The camera could not restart. Check that it is plugged in and not used by another app.";
            Phase = KioskPhase.Unknown;
            var ticket = ++_returnTicket;
            _ = ReturnToIdleAsync(ticket, TimeSpan.FromSeconds(4), KioskPhase.Unknown);
        }
        finally
        {
            IsResettingCamera = false;
            FocusBadgeRequested?.Invoke();
            _resetCoolingDown = true;
            _resetCamera.RaiseCanExecuteChanged();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanResetCamera)));
            _ = CoolResetAsync();
        }
    }

    /// <summary>
    /// The clock-out that can still hold another tap: the server's, or one taken on this device that the
    /// server has not confirmed. A punch taken here counts even when the badge lookup says the day is not
    /// finished — offline the roster always says that, and dropping it skipped the hold entirely.
    /// </summary>
    private DateTimeOffset? LatestClockOut(CandidateBadge badge)
    {
        DateTimeOffset? latest = badge.ShiftFinished ? badge.FinishedClockOut : null;

        if (_lastClockOut.TryGetValue(badge.CandidateId, out DateTimeOffset local))
        {
            if (ClockCooldownPolicy.HasExpired(Now(), local))
                _lastClockOut.Remove(badge.CandidateId);
            else if (latest is null || local > latest)
                latest = local;
        }

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

    /// <summary>
    /// A card nobody Working can use. Shows its number for 10 seconds so staff can assign it in the ERP;
    /// the reader stays live, and the next tap replaces this dialog straight away.
    /// </summary>
    private void ShowRfidDialog(string rfid, bool reassign, string holderName, string holderStatus)
    {
        _badge = null;
        IsReassign = reassign;
        RfidNumber = rfid;
        RfidHolderName = holderName.Length > 0 ? $"Linked to {holderName}" : string.Empty;
        RfidHolderStatus = holderStatus;
        HasRfidHolder = holderName.Length > 0;
        HasRfidHolderStatus = holderStatus.Length > 0;
        RfidTitle = reassign ? "Reassign this RFID number" : "Assign this RFID number";
        RfidDetail = reassign
            ? "This card belongs to a candidate who is not Working. Reassign it to a Working candidate in the ERP."
            : "This card is not in the ERP yet. Assign it to a candidate in the ERP.";
        Phase = KioskPhase.Unassigned;
        var ticket = ++_returnTicket;
        _ = ReturnToIdleAsync(ticket, TimeSpan.FromSeconds(RfidDialogSeconds), KioskPhase.Unassigned);
    }

    private async Task CaptureAndPunchAsync(CandidateBadge badge, bool resetCompletedDay)
    {
        Phase = KioskPhase.Capturing;
        await Task.Delay(700);
        using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task<byte[]?> shot = _camera.CaptureJpegAsync(capture.Token);
        Task finished = await Task.WhenAny(shot, Task.Delay(TimeSpan.FromSeconds(9)));
        if (finished != shot)
        {
            capture.Cancel();
            _ = shot.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            UnknownMessage = "The camera did not respond. Hold the badge to the reader again.";
            Phase = KioskPhase.Unknown;
            var missed = ++_returnTicket;
            _ = ReturnToIdleAsync(missed, TimeSpan.FromSeconds(4), KioskPhase.Unknown);
            return;
        }

        byte[]? jpeg;
        try
        {
            jpeg = await shot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera capture failed");
            _camera.ReportFailure(ex);
            UnknownMessage = "The camera is reconnecting. Hold the badge to the reader again in a few seconds.";
            Phase = KioskPhase.Unknown;
            var failed = ++_returnTicket;
            _ = ReturnToIdleAsync(failed, TimeSpan.FromSeconds(4), KioskPhase.Unknown);
            return;
        }
        if (jpeg == null || jpeg.Length == 0)
        {
            UnknownMessage = "A photo is required to clock in or out.";
            Phase = KioskPhase.Unknown;
            var missing = ++_returnTicket;
            _ = ReturnToIdleAsync(missing, TimeSpan.FromSeconds(4), KioskPhase.Unknown);
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
        Phase = KioskPhase.Idle;
        FocusBadgeRequested?.Invoke();
    }

    private void Show(CandidateBadge badge)
    {
        _badge = badge;
        var openAt = _clocking.GetOpenClockIn(badge.CandidateId);
        IsOnShift = openAt.HasValue;
        CandidateName = badge.FullName;
        Initials = badge.Initials;
        AssignmentLine = $"{badge.Assignment}  ·  {badge.ClientName}  ·  {badge.Site}";
        StatusDetail = openAt is DateTimeOffset start
            ? $"Since {start.ToLocalTime():h:mm tt}  ·  {Format(Now() - start)} so far"
            : badge.ShiftFinished
                ? "Today's times stay. The next tap starts extra time."
                : "Ready to start a shift";
        ActionTitle = IsOnShift ? "Clock out" : badge.ShiftFinished ? "Extra time" : "Clock in";
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

    private bool SetFlag(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
            return false;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanResetCamera)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResetCameraText)));
        _resetCamera.RaiseCanExecuteChanged();
        return true;
    }

    private async Task<bool> WaitForLiveAsync(TimeSpan timeout)
    {
        if (_camera.Health == CameraHealth.Live)
            return true;

        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHealth(CameraHealth health)
        {
            if (health == CameraHealth.Live)
                live.TrySetResult();
        }

        _camera.HealthChanged += OnHealth;
        try
        {
            if (_camera.Health == CameraHealth.Live)
                return true;

            return await Task.WhenAny(live.Task, Task.Delay(timeout)) == live.Task;
        }
        finally
        {
            _camera.HealthChanged -= OnHealth;
        }
    }

    private async Task CoolResetAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        _resetCoolingDown = false;
        _resetCamera.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanResetCamera)));
    }

    private async Task MaybeDailyRebuildAsync()
    {
        var now = DateTime.Now;
        if (!CameraRecoveryPolicy.IsDailyRebuildDue(now, _lastDailyRebuild, _cameraSettings.DailyRebuildAt))
        {
            _dailyWaits = 0;
            return;
        }

        if (_dailyWaits >= 30)
            return;

        if (Phase != KioskPhase.Idle || _busy || DateTime.UtcNow - _lastTapUtc < TimeSpan.FromSeconds(60))
        {
            _dailyWaits++;
            return;
        }

        _dailyWaits++;
        try
        {
            await _camera.RebuildAsync("daily");
            _lastDailyRebuild = DateTime.Now;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Daily camera rebuild failed");
        }
    }

    private void LogTap(long started, string rfid, Exception? failure)
    {
        _logger.LogInformation(
            "Tap finished phase {Phase} elapsed {ElapsedMs} ms badge {Badge} exception {ExceptionType}",
            Phase,
            ElapsedMs(started),
            MaskRfid(rfid),
            failure?.GetType().Name ?? "none");
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static string MaskRfid(string rfid)
    {
        if (string.IsNullOrEmpty(rfid))
            return "****";
        var tail = rfid.Length <= 4 ? rfid : rfid[^4..];
        return "****" + tail;
    }
}

public sealed class ResetCameraCommand : ICommand
{
    private readonly KioskViewModel _owner;

    public ResetCameraCommand(KioskViewModel owner) => _owner = owner;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _owner.CanResetCamera;

    public void Execute(object? parameter) => _ = _owner.ResetCameraAsync();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
