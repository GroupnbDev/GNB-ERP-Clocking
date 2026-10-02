using Gnb.Clocking.App.Camera;
using Gnb.Clocking.App.Controls;
using Gnb.Clocking.App.Diagnostics;
using Gnb.Clocking.App.Theming;
using Gnb.Clocking.App.ViewModels;
using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Shapes;

namespace Gnb.Clocking.App.Pages;

public partial class KioskPage : ContentPage
{
    private const int MaxKeyGapMs = 120;
    private const int MaxBurstMs = 800;
    private const double ThemeSegmentWidth = 72;
    private const uint ReturnToIdleMs = 3600;
    private const string HoldNextPersonLoop = "hold-next-person";
    private const uint HoldWindowMs = (uint)(KioskViewModel.HoldSeconds * 1000);
    private const string RfidNextPersonLoop = "rfid-next-person";
    private const uint RfidWindowMs = (uint)(KioskViewModel.RfidDialogSeconds * 1000);

    private static readonly Color Crimson = ScannerHalo.Crimson;
    private static readonly Color Red = ScannerHalo.Red;
    private static readonly Color Slate = Color.FromArgb("#718096");

    private static readonly string[] StepNames = { "Scan badge", "Match", "Capture", "Saved" };

    private readonly KioskViewModel _viewModel;
    private readonly MauiClockCamera _camera;
    private readonly ILogger<KioskPage> _logger = KioskLog.Create<KioskPage>();
    private readonly List<(Border Pill, Border Dot, Label Label)> _steps = new();
    private readonly List<BoxView> _links = new();
    private readonly Dictionary<Label, int> _shownCounts = new();
    private CancellationTokenSource? _rfidIdle;
    private KioskPhase _lastPhase = KioskPhase.Idle;
    private int _motion;
    private bool _ambientStarted;
    private bool _clearing;
    private bool _compactLayout;
    private bool _densityApplied;
    private StageDensity _density = StageDensity.Roomy;
    private DateTime _burstStart;
    private DateTime _lastKey;

    public KioskPage(KioskViewModel viewModel, MauiClockCamera camera)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _camera = camera;
        BindingContext = viewModel;
        viewModel.FocusBadgeRequested += FocusBadge;

        BuildStepper();

        camera.Attach(ClockCamera);
        camera.HealthChanged += OnCameraHealthChanged;
        ResetCameraButton.HandlerChanged += (_, _) => ApplyResetCursor(_viewModel.CanResetCamera);
        ClockCamera.PropertyChanged += OnCameraPropertyChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        PaintTheme(animate: false);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.Start();
        StartAmbient();
        PaintPhase(_viewModel.Phase);
        PaintAction();
        PaintServer();
        PaintCamera();
        PaintTheme(animate: false);
        SetCount(OnShiftValue, _viewModel.OnShiftCount, animate: false);
        SetCount(InValue, _viewModel.ClockInsToday, animate: false);
        SetCount(OutValue, _viewModel.ClockOutsToday, animate: false);
        if (Microsoft.Maui.Controls.Application.Current != null)
            Microsoft.Maui.Controls.Application.Current.RequestedThemeChanged += OnRequestedThemeChanged;
        MotionSettings.Changed += OnMotionChanged;
        if (Window != null)
        {
            Window.Stopped += OnWindowStopped;
            Window.Resumed += OnWindowResumed;
            Window.Activated += OnWindowActivated;
        }
        RfidEntry.Unfocused += OnRfidUnfocused;
        Dispatcher.Dispatch(FocusBadge);
#if WINDOWS
        Platforms.Windows.FullScreenToggle.ResetCameraRequested += OnResetCameraKey;
#endif
    }

    private void OnRfidUnfocused(object? sender, FocusEventArgs e) => BadgeEntrySetup.ClaimSoon();

    private void OnResetCameraTapped(object? sender, TappedEventArgs e)
    {
        FocusBadge();
        if (_viewModel.ResetCameraCommand.CanExecute(null))
            _viewModel.ResetCameraCommand.Execute(null);
    }

    private void OnResetCameraPointerEntered(object? sender, PointerEventArgs e)
    {
        _resetHovered = true;
        ShowResetHover(_viewModel.CanResetCamera);
    }

    private void OnResetCameraPointerExited(object? sender, PointerEventArgs e)
    {
        _resetHovered = false;
        ShowResetHover(false);
    }

    private void OnResetCameraKey() => MainThread.BeginInvokeOnMainThread(() => OnResetCameraTapped(this, null!));

    private void OnCameraHealthChanged(CameraHealth health)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PaintCamera();
            if (health == CameraHealth.Live)
                FocusBadge();
        });
    }

    protected override void OnDisappearing()
    {
        if (Microsoft.Maui.Controls.Application.Current != null)
            Microsoft.Maui.Controls.Application.Current.RequestedThemeChanged -= OnRequestedThemeChanged;
        MotionSettings.Changed -= OnMotionChanged;
        if (Window != null)
        {
            Window.Stopped -= OnWindowStopped;
            Window.Resumed -= OnWindowResumed;
            Window.Activated -= OnWindowActivated;
        }
        RfidEntry.Unfocused -= OnRfidUnfocused;
#if WINDOWS
        Platforms.Windows.FullScreenToggle.ResetCameraRequested -= OnResetCameraKey;
#endif
        StopAmbient();
        base.OnDisappearing();
    }

    // Minimized or hidden: nobody can see the motion, so spend nothing on it.
    private void OnWindowStopped(object? sender, EventArgs e)
    {
        _logger.LogInformation("Window Stopped");
        Aurora.Pause();
        Halo.Pause();
        StopAmbient();
    }

    /// <summary>Stepped down to Lite at run time: restart the ambient loops with the lighter set.</summary>
    private void OnMotionChanged()
    {
        StopAmbient();
        StartAmbient();
        PaintPhase(_viewModel.Phase);
        PaintServer();
        Aurora.Pause();
        Aurora.Resume();
        Halo.Pause();
        Halo.Resume();
    }

    private void OnWindowResumed(object? sender, EventArgs e)
    {
        _logger.LogInformation("Window Resumed");
        Aurora.Resume();
        Halo.Resume();
        StartAmbient();
        FocusBadge();
        _ = _camera.RebuildAsync("window-resumed");
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _logger.LogInformation("Window Activated");
        FocusBadge();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(KioskViewModel.Phase):
                _ = AnimatePhaseAsync(_viewModel.Phase);
                break;
            case nameof(KioskViewModel.IsOnShift):
                PaintAction();
                break;
            case nameof(KioskViewModel.ColonOn):
                if (MotionSettings.IsLite)
                    Colon.Opacity = _viewModel.ColonOn ? 1 : 0.25;
                else
                    Colon.FadeTo(_viewModel.ColonOn ? 1 : 0.25, 380, Easing.SinInOut);
                break;
            case nameof(KioskViewModel.ClockMinutes):
                _ = RollInAsync(MinutesLabel);
                break;
            case nameof(KioskViewModel.ClockHours):
                _ = RollInAsync(HoursLabel);
                break;
            case nameof(KioskViewModel.Prompt):
                _ = RiseInAsync(PromptStack);
                break;
            case nameof(KioskViewModel.Server):
                PaintServer();
                break;
            case nameof(KioskViewModel.CanResetCamera):
            case nameof(KioskViewModel.ResetCameraText):
            case nameof(KioskViewModel.IsResettingCamera):
            case nameof(KioskViewModel.IsBusy):
                PaintResetCamera();
                if (!_viewModel.IsBusy)
                    FocusBadge();
                break;
            case nameof(KioskViewModel.OnShiftCount):
                SetCount(OnShiftValue, _viewModel.OnShiftCount, animate: true);
                break;
            case nameof(KioskViewModel.ClockInsToday):
                SetCount(InValue, _viewModel.ClockInsToday, animate: true);
                break;
            case nameof(KioskViewModel.ClockOutsToday):
                SetCount(OutValue, _viewModel.ClockOutsToday, animate: true);
                break;
        }
    }

    // ------------------------------------------------------------------ RFID reader

    private void OnRfidTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_clearing)
            return;

        _rfidIdle?.Cancel();
        var now = DateTime.UtcNow;
        var text = e.NewTextValue ?? string.Empty;
        var previous = e.OldTextValue ?? string.Empty;

        if (text.Length < previous.Length)
        {
            ClearCapture();
            return;
        }

        if (text.Length == 0)
            return;

        if (previous.Length == 0)
            _burstStart = now;
        else if ((now - _lastKey).TotalMilliseconds > MaxKeyGapMs)
        {
            ClearCapture();
            return;
        }

        _lastKey = now;
        _rfidIdle = new CancellationTokenSource();
        _ = SubmitWhenReaderPausesAsync(_rfidIdle.Token);
    }

    private void OnRfidCompleted(object? sender, EventArgs e)
    {
        _rfidIdle?.Cancel();
        AcceptBurst();
    }

    private async Task SubmitWhenReaderPausesAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        AcceptBurst();
    }

    private void AcceptBurst()
    {
        var text = RfidEntry.Text ?? string.Empty;
        var elapsed = _burstStart == default ? TimeSpan.MaxValue : DateTime.UtcNow - _burstStart;
        var rfid = RfidNormalizer.Normalize(text);
        ClearCapture();

        if (rfid.Length < 8 || elapsed.TotalMilliseconds > MaxBurstMs)
            return;

        _viewModel.BadgeText = rfid;
        _ = _viewModel.SubmitAsync();
        FocusBadge();
    }

    private void ClearCapture()
    {
        _rfidIdle?.Cancel();
        _clearing = true;
        _burstStart = default;
        _lastKey = default;
        RfidEntry.Text = string.Empty;
        _viewModel.BadgeText = string.Empty;
        _clearing = false;
    }

    private void FocusBadge()
    {
        if (!RfidEntry.IsFocused)
            RfidEntry.Focus();
        BadgeEntrySetup.ClaimSoon();
    }

    /// <summary>
    /// Cooldown card: a tap that came too soon. Neutral, not a status colour — nothing was saved and
    /// nothing went wrong. The bar runs for the same window the card stays up.
    /// </summary>
    private async Task ShowHoldAsync()
    {
        if (HoldOverlay.IsVisible)
            return;

        HoldOverlay.CancelAnimations();
        HoldCard.CancelAnimations();
        HoldAutoFill.CancelAnimations();

        HoldOverlay.IsVisible = true;
        HoldScrim.Opacity = 0;
        HoldCard.Opacity = 0;
        HoldCard.Scale = 0.94;
        HoldCard.TranslationY = 26;
        HoldAutoFill.ScaleX = 1;

        if (MotionSettings.IsLite)
            HoldNextPersonPulse.Opacity = 0;
        else
            Ripple(HoldNextPersonPulse, HoldNextPersonLoop, 1800, 1.55);

        _ = HoldAutoFill.ScaleXTo(0, HoldWindowMs, Easing.Linear);
        await Task.WhenAll(
            HoldScrim.FadeTo(0.72, 200, Easing.CubicOut),
            HoldCard.FadeTo(1, 220, Easing.CubicOut),
            HoldCard.ScaleTo(1, 420, Easing.SpringOut),
            HoldCard.TranslateTo(0, 0, 420, Easing.CubicOut));
        FocusBadge();
    }

    /// <summary>
    /// RFID dialog: the card is not linked to a Working candidate. Warning for both variants (staff must act);
    /// the colours live in XAML tokens and only the glyph differs. Same entrance as the cooldown card with a
    /// 10 s bar; a new tap moves the phase on and closes it.
    /// </summary>
    private async Task ShowRfidAsync(int motion)
    {
        RfidIconLabel.Text = _viewModel.IsReassign ? "!" : "+";

        RfidOverlay.CancelAnimations();
        RfidCard.CancelAnimations();
        RfidAutoFill.CancelAnimations();

        RfidOverlay.IsVisible = true;
        RfidScrim.Opacity = 0;
        RfidCard.Opacity = 0;
        RfidCard.Scale = 0.94;
        RfidCard.TranslationY = 26;
        RfidCard.TranslationX = 0;
        RfidNumberChip.Scale = 0.9;
        RfidAutoFill.ScaleX = 1;

        if (MotionSettings.IsLite)
            RfidNextPersonPulse.Opacity = 0;
        else
            Ripple(RfidNextPersonPulse, RfidNextPersonLoop, 1800, 1.55);

        _ = RfidAutoFill.ScaleXTo(0, RfidWindowMs, Easing.Linear);
        await Task.WhenAll(
            RfidScrim.FadeTo(0.72, 200, Easing.CubicOut),
            RfidCard.FadeTo(1, 220, Easing.CubicOut),
            RfidCard.ScaleTo(1, 420, Easing.SpringOut),
            RfidCard.TranslateTo(0, 0, 420, Easing.CubicOut),
            RfidNumberChip.ScaleTo(1, 520, Easing.SpringOut));
        FocusBadge();
        if (motion == _motion && _viewModel.IsReassign)
            await ShakeAsync(RfidNumberChip);
    }

    private async Task HideRfidAsync()
    {
        if (!RfidOverlay.IsVisible)
            return;

        RfidAutoFill.CancelAnimations();
        RfidNextPersonPulse.AbortAnimation(RfidNextPersonLoop);
        RfidNextPersonPulse.Scale = 1;
        RfidNextPersonPulse.Opacity = 0;
        await Task.WhenAll(
            RfidScrim.FadeTo(0, 160, Easing.CubicIn),
            RfidCard.FadeTo(0, 160, Easing.CubicIn),
            RfidCard.ScaleTo(0.96, 160, Easing.CubicIn));
        // A new tap may have reopened it while this faded out.
        if (_viewModel.Phase != KioskPhase.Unassigned)
            RfidOverlay.IsVisible = false;
    }

    /// <summary>Staff can close the dialog early by touching outside it; the reader keeps focus.</summary>
    private void OnRfidScrimTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel.Phase == KioskPhase.Unassigned)
            _viewModel.Dismiss();
        FocusBadge();
    }

    private async Task HideHoldAsync()
    {
        if (!HoldOverlay.IsVisible)
            return;

        HoldAutoFill.CancelAnimations();
        HoldNextPersonPulse.AbortAnimation(HoldNextPersonLoop);
        HoldNextPersonPulse.Scale = 1;
        HoldNextPersonPulse.Opacity = 0;
        await Task.WhenAll(
            HoldScrim.FadeTo(0, 160, Easing.CubicIn),
            HoldCard.FadeTo(0, 160, Easing.CubicIn),
            HoldCard.ScaleTo(0.96, 160, Easing.CubicIn));
        HoldOverlay.IsVisible = false;
    }

    // ------------------------------------------------------------------ Phase choreography

    private async Task AnimatePhaseAsync(KioskPhase phase)
    {
        var motion = ++_motion;
        var previous = _lastPhase;
        _lastPhase = phase;
        PaintPhase(phase, previous);
        PaintAction();

        switch (phase)
        {
            case KioskPhase.Reading:
                Halo.SetMode(HaloMode.Reading);
                await Task.WhenAll(HideHoldAsync(), HideRfidAsync());
                Aurora.Flash(Crimson);
                HideBanner();
                await Task.WhenAll(HideSuccessAsync(), HideAsync(IdentityChip));
                StartScanLine();
                await PunchCameraAsync(1.03);
                break;

            case KioskPhase.Capturing:
            case KioskPhase.Recognized:
                Halo.SetMode(HaloMode.Capturing);
                await Task.WhenAll(HideHoldAsync(), HideRfidAsync());
                HideBanner();
                StartScanLine();
                await ShowIdentityAsync();
                _ = FlashShutterWhenCapturedAsync(motion);
                break;

            case KioskPhase.Hold:
                Halo.SetMode(HaloMode.Capturing);
                await HideRfidAsync();
                HideBanner();
                StopScanLine();
                await ShowHoldAsync();
                break;

            case KioskPhase.Unassigned:
                // Nothing failed, so no error pulse or flash: the dialog is the only emphasis.
                Halo.SetMode(HaloMode.Capturing);
                await HideHoldAsync();
                HideBanner();
                StopScanLine();
                PaintCameraRing(StatusColors.WarningSolid);
                await Task.WhenAll(HideSuccessAsync(), HideAsync(IdentityChip));
                if (motion != _motion)
                    return;
                await ShowRfidAsync(motion);
                break;

            case KioskPhase.Success:
                Halo.SetMode(HaloMode.Success);
                await Task.WhenAll(HideHoldAsync(), HideRfidAsync());
                Aurora.Flash(StatusColors.SuccessSolid);
                HideBanner();
                StopScanLine();
                PaintCameraRing(StatusColors.SuccessSolid);
                await HideAsync(IdentityChip);
                if (motion != _motion)
                    return;
                await ShowSuccessAsync();
                break;

            case KioskPhase.Unknown:
                Halo.SetMode(HaloMode.Error);
                await Task.WhenAll(HideHoldAsync(), HideRfidAsync());
                Aurora.Flash(StatusColors.ErrorSolid);
                StopScanLine();
                PaintCameraRing(StatusColors.ErrorSolid);
                await Task.WhenAll(HideSuccessAsync(), HideAsync(IdentityChip));
                if (motion != _motion)
                    return;
                await Task.WhenAll(ShowBannerAsync(), ShakeAsync(CameraFrame));
                _ = HideBannerLaterAsync(motion);
                break;

            default:
                Halo.SetMode(HaloMode.Idle);
                await Task.WhenAll(HideHoldAsync(), HideRfidAsync());
                HideBanner();
                StopScanLine();
                PaintCameraRing(null);
                await Task.WhenAll(HideSuccessAsync(), HideAsync(IdentityChip));
                break;
        }
    }

    private async Task ShowIdentityAsync()
    {
        if (IdentityChip.IsVisible)
            return;

        IdentityChip.IsVisible = true;
        IdentityChip.Opacity = 0;
        IdentityChip.TranslationY = 36;
        IdentityChip.Scale = 0.92;
        await Task.WhenAll(
            IdentityChip.FadeTo(1, 220, Easing.CubicOut),
            IdentityChip.TranslateTo(0, 0, 460, Easing.SpringOut),
            IdentityChip.ScaleTo(1, 380, Easing.CubicOut));
    }

    private async Task ShowSuccessAsync()
    {
        var photo = _viewModel.HasSuccessPhoto;
        SuccessPhotoImage.IsVisible = photo;
        SuccessPhotoImage.Opacity = 0;
        SuccessPhotoImage.Scale = 1.15;

        var offset = CameraFrame.WidthRequest / 2 * 0.72;
        SuccessBadge.TranslationX = offset;
        SuccessBadge.TranslationY = offset;
        SuccessBadge.IsVisible = true;
        SuccessBadge.Opacity = 0;
        SuccessBadge.Scale = 0.2;
        SuccessBadge.Rotation = -90;

        PromptStack.CancelAnimations();
        SuccessPanel.IsVisible = true;
        SuccessPanel.Opacity = 0;
        SuccessPanel.TranslationY = 18;
        CountdownFill.CancelAnimations();
        CountdownFill.ScaleX = 1;

        _ = CountdownFill.ScaleXTo(0, ReturnToIdleMs, Easing.Linear);
        await Task.WhenAll(
            PromptStack.FadeTo(0, 140, Easing.CubicIn),
            SuccessPanel.FadeTo(1, 260, Easing.CubicOut),
            SuccessPanel.TranslateTo(0, 0, 420, Easing.CubicOut),
            photo ? SuccessPhotoImage.FadeTo(1, 260, Easing.CubicOut) : Task.CompletedTask,
            photo ? SuccessPhotoImage.ScaleTo(1, 520, Easing.CubicOut) : Task.CompletedTask,
            SuccessBadge.FadeTo(1, 160),
            SuccessBadge.ScaleTo(1, 520, Easing.SpringOut),
            SuccessBadge.RotateTo(0, 420, Easing.CubicOut),
            PunchCameraAsync(1.05));
        PromptStack.IsVisible = false;
    }

    private async Task HideSuccessAsync()
    {
        if (!SuccessPanel.IsVisible && !SuccessBadge.IsVisible && PromptStack.IsVisible)
            return;

        PromptStack.IsVisible = true;
        await Task.WhenAll(
            SuccessPanel.FadeTo(0, 140, Easing.CubicIn),
            SuccessBadge.FadeTo(0, 140, Easing.CubicIn),
            SuccessPhotoImage.FadeTo(0, 180, Easing.CubicIn),
            PromptStack.FadeTo(1, 220, Easing.CubicOut));
        SuccessPanel.IsVisible = false;
        SuccessBadge.IsVisible = false;
        SuccessPhotoImage.IsVisible = false;
    }

    private async Task ShowBannerAsync()
    {
        UnknownBanner.CancelAnimations();
        UnknownBanner.IsVisible = true;
        UnknownBanner.Opacity = 0;
        UnknownBanner.TranslationY = -16;
        UnknownBanner.TranslationX = 0;
        await Task.WhenAll(
            UnknownBanner.FadeTo(1, 160),
            UnknownBanner.TranslateTo(0, 0, 320, Easing.SpringOut));
        await ShakeAsync(UnknownBanner);
    }

    private async Task HideBannerLaterAsync(int motion)
    {
        await Task.Delay(6000);
        if (motion != _motion || !UnknownBanner.IsVisible)
            return;

        await UnknownBanner.FadeTo(0, 300, Easing.CubicIn);
        if (motion == _motion)
        {
            HideBanner();
            Halo.SetMode(HaloMode.Idle);
            PaintCameraRing(null);
            PaintPhase(KioskPhase.Idle);
        }
    }

    private void HideBanner()
    {
        UnknownBanner.CancelAnimations();
        UnknownBanner.IsVisible = false;
        UnknownBanner.Opacity = 1;
    }

    private async Task FlashShutterWhenCapturedAsync(int motion)
    {
        await Task.Delay(TimeSpan.FromSeconds(ScannerHalo.CaptureSeconds - 0.05));
        if (motion != _motion)
            return;

        Shutter.Opacity = 0.92;
        await Task.WhenAll(
            Shutter.FadeTo(0, 320, Easing.CubicOut),
            PunchCameraAsync(0.97));
    }

    private async Task PunchCameraAsync(double scale)
    {
        await CameraFrame.ScaleTo(scale, 120, Easing.CubicOut);
        await CameraFrame.ScaleTo(1, 360, Easing.SpringOut);
    }

    private static async Task ShakeAsync(VisualElement view)
    {
        foreach (var x in new[] { -12.0, 10, -7, 5, -2, 0 })
            await view.TranslateTo(x, view.TranslationY, 45, Easing.SinInOut);
    }

    private static async Task HideAsync(VisualElement view)
    {
        if (!view.IsVisible)
            return;

        view.CancelAnimations();
        await Task.WhenAll(
            view.FadeTo(0, 160, Easing.CubicIn),
            view.TranslateTo(0, 16, 160, Easing.CubicIn));
        view.IsVisible = false;
        view.Opacity = 1;
        view.Scale = 1;
        view.TranslationY = 0;
    }

    private static async Task RiseInAsync(VisualElement view)
    {
        view.CancelAnimations();
        view.Opacity = 0;
        view.TranslationY = 14;
        await Task.WhenAll(
            view.FadeTo(1, 260, Easing.CubicOut),
            view.TranslateTo(0, 0, 360, Easing.CubicOut));
    }

    private static async Task RollInAsync(VisualElement view)
    {
        view.CancelAnimations();
        view.Opacity = 0;
        view.TranslationY = -12;
        await Task.WhenAll(
            view.FadeTo(1, 240, Easing.CubicOut),
            view.TranslateTo(0, 0, 420, Easing.SpringOut));
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0)
            return;

        // 1366×768 and 1600×769 panels are short once the header, stepper and prompt are on screen;
        // below ~700 the roomy sizes do not fit at all, so there is a third, tighter step.
        ApplyDensity(height <= 800 ? (height <= 700 ? StageDensity.Tight : StageDensity.Compact) : StageDensity.Roomy);
    }

    /// <summary>
    /// Short windows keep the stepper above the camera and still leave a circle that fits.
    /// Taller windows keep the roomier type and spacing.
    /// </summary>
    private void ApplyDensity(StageDensity density)
    {
        if (_densityApplied && density == _density)
            return;

        _densityApplied = true;
        _density = density;
        _compactLayout = density != StageDensity.Roomy;

        if (density == StageDensity.Tight)
        {
            // ~700px and under: every fixed height has to give, or the prompt falls off the screen.
            PageGrid.Padding = new Thickness(12, 6, 12, 6);
            PageGrid.RowSpacing = 6;
            StageGrid.Padding = new Thickness(12, 8, 12, 8);
            StageGrid.RowSpacing = 4;
            StepperRow.Padding = new Thickness(0, 0, 0, 6);
            PromptBand.HeightRequest = 92;
            PromptTitle.FontSize = 19;
            PromptDetail.FontSize = 13;
            SuccessTimeLabel.FontSize = 32;
            HoursLabel.FontSize = 28;
            MinutesLabel.FontSize = 28;
            Colon.FontSize = 24;
            ActivityCard.Padding = new Thickness(12, 10, 12, 4);
            ActivityGrid.RowSpacing = 8;
            ApplyActivityScale(
                title: 18, caption: 11, value: 20, tileLabel: 10,
                avatar: 32, name: 13, detail: 11, time: 12, badge: 10,
                rowPadding: new Thickness(8, 7), tilePadding: new Thickness(10, 8), shortTileLabels: true);
            // A narrow rail is what truncates names, so give it a bigger share when space is short.
            SetBodyColumns(1.4, 10);
            return;
        }

        if (density == StageDensity.Compact)
        {
            PageGrid.Padding = new Thickness(16, 10, 16, 8);
            PageGrid.RowSpacing = 8;
            StageGrid.Padding = new Thickness(16, 10, 16, 10);
            StageGrid.RowSpacing = 6;
            StepperRow.Padding = new Thickness(0, 2, 0, 12);
            PromptBand.HeightRequest = 120;
            PromptTitle.FontSize = 22;
            PromptDetail.FontSize = 15;
            SuccessTimeLabel.FontSize = 40;
            HoursLabel.FontSize = 34;
            MinutesLabel.FontSize = 34;
            Colon.FontSize = 30;
            ActivityCard.Padding = new Thickness(16, 14, 16, 6);
            ActivityGrid.RowSpacing = 10;
            ApplyActivityScale(
                title: 20, caption: 12, value: 24, tileLabel: 11,
                avatar: 38, name: 14, detail: 12, time: 13, badge: 11,
                rowPadding: new Thickness(10, 9), tilePadding: new Thickness(12, 10), shortTileLabels: true);
            SetBodyColumns(1.55, 14);
            return;
        }

        PageGrid.Padding = new Thickness(28, 20, 28, 14);
        PageGrid.RowSpacing = 18;
        StageGrid.Padding = new Thickness(24, 16, 24, 16);
        StageGrid.RowSpacing = 12;
        StepperRow.Padding = new Thickness(0, 4, 0, 16);
        PromptBand.HeightRequest = 148;
        PromptTitle.FontSize = 28;
        PromptDetail.FontSize = 18;
        SuccessTimeLabel.FontSize = 58;
        HoursLabel.FontSize = 44;
        MinutesLabel.FontSize = 44;
        Colon.FontSize = 40;
        ActivityCard.Padding = new Thickness(22, 22, 22, 8);
        ActivityGrid.RowSpacing = 18;
        ApplyActivityScale(
            title: 24, caption: 14, value: 30, tileLabel: 13,
            avatar: 46, name: 16, detail: 13, time: 15, badge: 12,
            rowPadding: new Thickness(12), tilePadding: new Thickness(14, 12), shortTileLabels: false);
        SetBodyColumns(1.75, 20);
    }

    private void SetBodyColumns(double stageShare, double spacing)
    {
        BodyGrid.ColumnSpacing = spacing;
        BodyGrid.ColumnDefinitions = new ColumnDefinitionCollection(
            new ColumnDefinition(new GridLength(stageShare, GridUnitType.Star)),
            new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
    }

    /// <summary>
    /// The activity rail sizes itself with the window. The rows live in a DataTemplate, so their sizes
    /// come from app resources rather than named elements: on a short panel the avatar, name and badge
    /// all shrink together, which is what stops every name truncating to "Loremae Pa…".
    /// </summary>
    private void ApplyActivityScale(
        double title,
        double caption,
        double value,
        double tileLabel,
        double avatar,
        double name,
        double detail,
        double time,
        double badge,
        Thickness rowPadding,
        Thickness tilePadding,
        bool shortTileLabels)
    {
        var resources = Microsoft.Maui.Controls.Application.Current?.Resources;
        if (resources == null)
            return;

        resources["ActivityTitleSize"] = title;
        resources["ActivityCaptionSize"] = caption;
        resources["ActivityValueSize"] = value;
        resources["ActivityTileLabelSize"] = tileLabel;
        resources["RowAvatarSize"] = avatar;
        resources["RowNameSize"] = name;
        resources["RowDetailSize"] = detail;
        resources["RowTimeSize"] = time;
        resources["RowBadgeSize"] = badge;
        resources["RowPadding"] = rowPadding;
        resources["ActivityTilePadding"] = tilePadding;

        // "In today" wraps to two lines in a narrow tile; the tile header already says what day it is.
        OnShiftLabel.Text = "On shift";
        InLabel.Text = shortTileLabels ? "In" : "In today";
        OutLabel.Text = shortTileLabels ? "Out" : "Out today";
    }

    // ------------------------------------------------------------------ Scanner sizing and scan line

    private void OnScannerSizeChanged(object? sender, EventArgs e)
    {
        var width = ScannerArea.Width;
        var height = ScannerArea.Height;
        if (width <= 0 || height <= 0)
            return;

        // The glow tile is 1.95× the face. Fit that whole tile in the well so the ring is
        // complete at the top and bottom, and does not run under the stepper.
        const double haloScale = 1.95;
        const double margin = 12;
        var box = Math.Max(0, Math.Min(width, height) - margin * 2);
        var cap = _density switch
        {
            StageDensity.Tight => 300,
            StageDensity.Compact => 420,
            _ => 560,
        };
        var diameter = Math.Min(box / haloScale, cap);
        CameraFrame.WidthRequest = diameter;
        CameraFrame.HeightRequest = diameter;
        CameraFrame.StrokeShape = new RoundRectangle { CornerRadius = diameter / 2 };
        Shutter.StrokeShape = new RoundRectangle { CornerRadius = diameter / 2 };

        Halo.Resize(diameter);
        ScanLine.HeightRequest = diameter * 0.3;

        if (Width > 0 && Height > 0)
        {
            var origin = OffsetInPage(ScannerArea);
            Aurora.FlashOrigin = new Point((origin.X + width / 2) / Width, (origin.Y + height / 2) / Height);
        }
    }

    private Point OffsetInPage(VisualElement view)
    {
        double x = 0, y = 0;
        for (Element? e = view; e is VisualElement v && e != this; e = e.Parent)
        {
            x += v.X;
            y += v.Y;
        }

        return new Point(x, y);
    }

    private void StartScanLine()
    {
        if (MotionSettings.Level == MotionLevel.Off)
            return;

        var travel = CameraFrame.HeightRequest / 2 + ScanLine.HeightRequest / 2;
        ScanLine.AbortAnimation("scan");
        ScanLine.FadeTo(1, 160);
        new Animation(v => ScanLine.TranslationY = v, -travel, travel, Easing.SinInOut)
            .Commit(ScanLine, "scan", length: 1100, repeat: () => true);
    }

    private void StopScanLine()
    {
        ScanLine.AbortAnimation("scan");
        ScanLine.FadeTo(0, 200);
    }

    private void PaintCameraRing(Color? solid)
    {
        CameraFrame.Stroke = solid != null
            ? new SolidColorBrush(solid)
            : new LinearGradientBrush(
                new GradientStopCollection { new GradientStop(Crimson, 0), new GradientStop(Red, 1) },
                new Point(0, 0), new Point(1, 1));
    }

    // ------------------------------------------------------------------ Stepper and chips

    private void BuildStepper()
    {
        for (var i = 0; i < StepNames.Length; i++)
        {
            if (i > 0)
            {
                var link = new BoxView { WidthRequest = 28, HeightRequest = 2, VerticalOptions = LayoutOptions.Center, CornerRadius = 1 };
                _links.Add(link);
                Stepper.Children.Add(link);
            }

            var dot = new Border
            {
                WidthRequest = 8,
                HeightRequest = 8,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 4 },
                VerticalOptions = LayoutOptions.Center,
            };
            var label = new Label { Text = StepNames[i], FontSize = 13, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center };
            var pill = new Border
            {
                Padding = new Thickness(12, 7),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 15 },
                VerticalOptions = LayoutOptions.Center,
                Content = new HorizontalStackLayout
                {
                    Spacing = 8,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { dot, label },
                },
            };
            _steps.Add((pill, dot, label));
            Stepper.Children.Add(pill);
        }
    }

    private void PaintPhase(KioskPhase phase, KioskPhase previous = KioskPhase.Idle)
    {
        // Brand red is the flow (ready, reading, capturing); status colours only for outcomes.
        // Solid chips carry their OnSolid text; the warning chip is a soft tint with WarningText.
        var (text, fill, stroke, ink) = phase switch
        {
            KioskPhase.Reading => ("Reading badge", Crimson, Crimson, Colors.White),
            KioskPhase.Capturing or KioskPhase.Recognized => ("Capturing photo", Crimson, Crimson, Colors.White),
            KioskPhase.Hold => ("Please wait", StatusColors.NeutralSolid, StatusColors.NeutralSolid, StatusColors.NeutralOnSolid),
            KioskPhase.Success => ("Punch saved", StatusColors.SuccessSolid, StatusColors.SuccessSolid, StatusColors.SuccessOnSolid),
            KioskPhase.Unknown => ("Needs attention", StatusColors.ErrorSolid, StatusColors.ErrorSolid, StatusColors.ErrorOnSolid),
            KioskPhase.Unassigned => (_viewModel.IsReassign ? "Reassign card" : "Card not assigned",
                StatusColors.WarningSoft, StatusColors.WarningBorder, StatusColors.WarningText),
            _ => ("Ready", Crimson, Crimson, Colors.White),
        };
        PhaseLabel.Text = text;
        PhaseLabel.TextColor = ink;
        PhaseDot.BackgroundColor = ink;
        PhaseChip.BackgroundColor = fill;
        PhaseChip.Stroke = stroke;
        PhaseDot.AbortAnimation("phase-dot");
        PhaseDot.Opacity = 1;
        if (phase is KioskPhase.Idle or KioskPhase.Reading or KioskPhase.Capturing or KioskPhase.Recognized or KioskPhase.Hold)
            LoopPulse(PhaseDot, "phase-dot", phase == KioskPhase.Idle ? 1600u : 600u, 1, 0.25);

        // Which step is active, where a failure happened (error), and where the flow stopped for staff (warning).
        var active = phase switch
        {
            KioskPhase.Reading => 1,
            KioskPhase.Capturing or KioskPhase.Recognized => 2,
            KioskPhase.Success => 4,
            _ => 0,
        };
        var failed = phase == KioskPhase.Unknown
            ? previous is KioskPhase.Capturing or KioskPhase.Recognized ? 2 : 1
            : -1;
        var warned = phase == KioskPhase.Unassigned ? 1 : -1;
        var stopped = Math.Max(failed, warned);
        if (stopped >= 0)
            active = stopped;

        var faint = Resource("Faint", Colors.Gray);
        var line = Resource("Line", Colors.Gray);
        for (var i = 0; i < _steps.Count; i++)
        {
            var (pill, dot, label) = _steps[i];
            dot.AbortAnimation("step");
            dot.Opacity = 1;
            Color stepFill;
            Color stepInk;
            if (i == failed)
                (stepFill, stepInk) = (StatusColors.ErrorSolid, StatusColors.ErrorOnSolid);
            else if (i == warned)
                (stepFill, stepInk) = (StatusColors.WarningSolid, StatusColors.WarningOnSolid);
            else if (i < active)
                (stepFill, stepInk) = (StatusColors.SuccessSolid, StatusColors.SuccessOnSolid);
            else if (i == active)
                (stepFill, stepInk) = (Crimson, Colors.White);
            else
                (stepFill, stepInk) = (faint, faint);

            var lit = i <= active;
            pill.BackgroundColor = lit ? stepFill : Colors.Transparent;
            pill.Stroke = lit ? stepFill : line;
            dot.BackgroundColor = lit ? stepInk : faint;
            label.TextColor = lit ? stepInk : faint;
            if (i == active && stopped < 0 && phase != KioskPhase.Success)
                LoopPulse(dot, "step", 700, 1, 0.2);

            if (i > 0)
            {
                var link = _links[i - 1];
                link.Color = i <= active && stopped < 0 || i < stopped ? StatusColors.SuccessSolid : line;
            }
        }

        if (phase is KioskPhase.Success)
            _ = CascadeStepsAsync();
    }

    private async Task CascadeStepsAsync()
    {
        foreach (var (pill, _, _) in _steps)
        {
            await pill.ScaleTo(1.08, 90, Easing.CubicOut);
            _ = pill.ScaleTo(1, 260, Easing.SpringOut);
        }
    }

    private void PaintAction()
    {
        var onShift = _viewModel.IsOnShift;
        var ink = onShift ? Red : Crimson;
        ActionPill.BackgroundColor = ink;
        ActionPill.Stroke = ink;
        ActionPillLabel.TextColor = Colors.White;
    }

    private void PaintServer()
    {
        var color = _viewModel.Server switch
        {
            ServerLink.Online => StatusColors.SuccessSolid,
            ServerLink.Offline => StatusColors.ErrorSolid,
            _ => Slate,
        };
        ServerDot.BackgroundColor = color;
        ServerDot.AbortAnimation("server");
        ServerDot.Opacity = 1;
        if (_viewModel.Server != ServerLink.Online)
            LoopPulse(ServerDot, "server", 900, 1, 0.25);
    }

    private void OnCameraPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ClockCameraView.StatusText))
            return;

        PaintCamera();
    }

    private void PaintCamera()
    {
        var live = _camera.Health == CameraHealth.Live;
        CameraFallback.Text = ClockCamera.StatusText;
        CameraFallback.IsVisible = !live;
        CameraChipLabel.Text = ClockCamera.StatusText;
        CameraDot.BackgroundColor = live ? StatusColors.SuccessSolid : Slate;
        PaintResetCamera();
    }

    private bool _resetHovered;
    private bool _resetHoverShown;

    private void PaintResetCamera()
    {
        ResetCameraLabel.Text = _viewModel.ResetCameraText;
        var enabled = _viewModel.CanResetCamera;
        ResetCameraButton.Opacity = enabled ? 1 : 0.45;
        ApplyResetCursor(enabled);
        ShowResetHover(_resetHovered && enabled);
    }

    private void ShowResetHover(bool hover)
    {
        if (hover == _resetHoverShown)
            return;

        _resetHoverShown = hover;
        var ink = Resource("Ink", Color.FromArgb("#1A202C"));
        var muted = Resource("Muted", Color.FromArgb("#4A5568"));
        ResetCameraButton.Stroke = Resource(hover ? "LineStrong" : "Line", muted);
        ResetCameraLabel.TextColor = hover ? ink : muted;
        ResetCameraGlyph.TextColor = hover ? ink : muted;
        ResetCameraButton.AbortAnimation("ScaleTo");
        _ = ResetCameraButton.ScaleTo(hover ? 1.06 : 1, 160, Easing.CubicOut);
    }

    private void ApplyResetCursor(bool hand)
    {
#if WINDOWS
        if (ResetCameraButton.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement element)
            return;

        // ProtectedCursor is protected on UIElement; a MAUI view's platform element is not ours to subclass.
        var cursor = Microsoft.UI.Input.InputSystemCursor.Create(
            hand
                ? Microsoft.UI.Input.InputSystemCursorShape.Hand
                : Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        typeof(Microsoft.UI.Xaml.UIElement)
            .GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(element, cursor);
#endif
    }

    // ------------------------------------------------------------------ Live activity

    private void OnPunchRowAttached(object? sender, EventArgs e)
    {
        if (sender is not VisualElement { BindingContext: PunchRow { IsFresh: true } row } view || view.Handler == null)
            return;

        row.IsFresh = false;
        view.Opacity = 0;
        view.TranslationX = 48;
        view.Scale = 0.96;
        _ = Task.WhenAll(
            view.FadeTo(1, 260, Easing.CubicOut),
            view.TranslateTo(0, 0, 520, Easing.SpringOut),
            view.ScaleTo(1, 420, Easing.CubicOut));
    }

    private void SetCount(Label label, int value, bool animate)
    {
        var from = _shownCounts.TryGetValue(label, out var shown) ? shown : 0;
        _shownCounts[label] = value;
        label.AbortAnimation("count");
        if (!animate || from == value)
        {
            label.Text = value.ToString();
            return;
        }

        new Animation(v => label.Text = ((int)Math.Round(v)).ToString(), from, value, Easing.CubicOut)
            .Commit(label, "count", length: 600);
        if (label.Parent?.Parent is VisualElement tile)
            _ = PopAsync(tile);
    }

    private static async Task PopAsync(VisualElement view)
    {
        await view.ScaleTo(1.06, 120, Easing.CubicOut);
        await view.ScaleTo(1, 380, Easing.SpringOut);
    }

    // ------------------------------------------------------------------ Theme switch

    private void OnThemeTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is string choice)
            AppearanceSettings.Set(choice);
        PaintTheme(animate: true);
        // Status colours painted in code read the refreshed tokens.
        PaintPhase(_viewModel.Phase);
        PaintServer();
        PaintCamera();
        BadgeEntrySetup.ClaimSoon();
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        Aurora.RefreshTheme();
        Halo.RefreshTheme();
        PaintTheme(animate: false);
        PaintPhase(_viewModel.Phase);
        PaintServer();
        PaintCamera();
    }

    private void PaintTheme(bool animate)
    {
        var selected = AppearanceSettings.Current;
        var index = selected switch
        {
            AppearanceSettings.Light => 0,
            AppearanceSettings.Dark => 1,
            _ => 2,
        };

        var x = index * ThemeSegmentWidth;
        if (animate)
            ThemeIndicator.TranslateTo(x, 0, 320, Easing.SpringOut);
        else
            ThemeIndicator.TranslationX = x;

        var muted = Resource("Muted", Colors.Gray);
        ThemeLight.TextColor = index == 0 ? Colors.White : muted;
        ThemeDark.TextColor = index == 1 ? Colors.White : muted;
        ThemeSystem.TextColor = index == 2 ? Colors.White : muted;
    }

    // ------------------------------------------------------------------ Ambient motion

    private void StartAmbient()
    {
        if (_ambientStarted || MotionSettings.Level == MotionLevel.Off)
            return;

        _ambientStarted = true;

        // Any looping animation keeps MAUI's display-rate ticker running. Lite has none at idle.
        if (MotionSettings.IsLite)
        {
            LiveRipple.Opacity = 0;
            return;
        }

        Ripple(LiveRipple, "live", 1600, 2.8);

        Ripple(FeedRipple, "feed", 2000, 2.6);
        Ripple(EmptyRingOuter, "empty-outer", 2600, 2.1);
        Ripple(EmptyRingInner, "empty-inner", 2600, 1.6);

        if (MotionSettings.Level == MotionLevel.Full)
        {
            var animation = new Animation();
            animation.Add(0, 0.5, new Animation(v => LogoGlow.Opacity = v, 0.2, 0.55, Easing.SinInOut));
            animation.Add(0.5, 1, new Animation(v => LogoGlow.Opacity = v, 0.55, 0.2, Easing.SinInOut));
            animation.Commit(LogoGlow, "logo-glow", length: 3200, repeat: () => true);
        }
    }

    private void StopAmbient()
    {
        _ambientStarted = false;
        LiveRipple.AbortAnimation("live");
        FeedRipple.AbortAnimation("feed");
        EmptyRingOuter.AbortAnimation("empty-outer");
        EmptyRingInner.AbortAnimation("empty-inner");
        LogoGlow.AbortAnimation("logo-glow");
        ScanLine.AbortAnimation("scan");
        foreach (var view in new VisualElement[] { LiveRipple, FeedRipple, EmptyRingOuter, EmptyRingInner })
        {
            view.Scale = 1;
            view.Opacity = 0;
        }
    }

    private static void Ripple(VisualElement view, string name, uint length, double scale)
    {
        view.AbortAnimation(name);
        new Animation
        {
            { 0, 1, new Animation(v => view.Scale = v, 1, scale, Easing.CubicOut) },
            { 0, 1, new Animation(v => view.Opacity = v, 0.7, 0, Easing.CubicIn) },
        }.Commit(view, name, length: length, repeat: () => true);
    }

    private static void LoopPulse(VisualElement view, string name, uint length, double from, double to)
    {
        view.AbortAnimation(name);
        if (MotionSettings.IsLite)
            return;

        var animation = new Animation();
        animation.Add(0, 0.5, new Animation(value => view.Opacity = value, from, to, Easing.SinInOut));
        animation.Add(0.5, 1, new Animation(value => view.Opacity = value, to, from, Easing.SinInOut));
        animation.Commit(view, name, length: length, repeat: () => true);
    }

    private static Color Resource(string key, Color fallback) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
            ? color
            : fallback;
}

/// <summary>How much room the window gives the stage. Picked from the window height, not the width.</summary>
internal enum StageDensity
{
    /// <summary>Tall enough for the roomy type and a large camera face.</summary>
    Roomy,

    /// <summary>1366×768-class panels: tighter spacing, smaller face.</summary>
    Compact,

    /// <summary>Under ~700px of window height: everything fixed has to shrink or the prompt is clipped.</summary>
    Tight,
}
