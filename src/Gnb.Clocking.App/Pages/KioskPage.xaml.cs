using Gnb.Clocking.App.Camera;
using Gnb.Clocking.App.Controls;
using Gnb.Clocking.App.Theming;
using Gnb.Clocking.App.ViewModels;
using Gnb.Clocking.Domain.Clocking;
using Microsoft.Maui.Controls.Shapes;

namespace Gnb.Clocking.App.Pages;

public partial class KioskPage : ContentPage
{
    private const int MaxKeyGapMs = 120;
    private const int MaxBurstMs = 800;
    private const double ThemeSegmentWidth = 72;
    private const uint ReturnToIdleMs = 3600;

    private static readonly Color Crimson = ScannerHalo.Crimson;
    private static readonly Color Red = ScannerHalo.Red;
    private static readonly Color Green = ScannerHalo.Green;
    private static readonly Color Alarm = ScannerHalo.Alarm;
    private static readonly Color Slate = Color.FromArgb("#718096");

    private static readonly string[] StepNames = { "Scan badge", "Match", "Capture", "Saved" };

    private readonly KioskViewModel _viewModel;
    private readonly List<(Border Pill, Border Dot, Label Label)> _steps = new();
    private readonly List<BoxView> _links = new();
    private readonly Dictionary<Label, int> _shownCounts = new();
    private CancellationTokenSource? _rfidIdle;
    private KioskPhase _lastPhase = KioskPhase.Idle;
    private int _motion;
    private bool _ambientStarted;
    private bool _clearing;
    private DateTime _burstStart;
    private DateTime _lastKey;

    public KioskPage(KioskViewModel viewModel, MauiClockCamera camera)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;

        BuildStepper();

        camera.Attach(ClockCamera);
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
        // The native field is not in the window yet during OnAppearing. Focus on the next turn.
        Dispatcher.Dispatch(FocusBadge);
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
        StopAmbient();
        base.OnDisappearing();
    }

    // Minimized or hidden: nobody can see the motion, so spend nothing on it.
    private void OnWindowStopped(object? sender, EventArgs e)
    {
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
        Aurora.Resume();
        Halo.Resume();
        StartAmbient();
        FocusBadge();
    }

    private void OnWindowActivated(object? sender, EventArgs e) => FocusBadge();

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
            case nameof(KioskViewModel.OnShiftCount):
                SetCount(OnShiftValue, _viewModel.OnShiftCount, animate: true);
                break;
            case nameof(KioskViewModel.ClockInsToday):
                SetCount(InValue, _viewModel.ClockInsToday, animate: true);
                break;
            case nameof(KioskViewModel.ClockOutsToday):
                SetCount(OutValue, _viewModel.ClockOutsToday, animate: true);
                break;
            case nameof(KioskViewModel.IsBusy) when !_viewModel.IsBusy:
                FocusBadge();
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
    }

    private async void ConfirmResetClicked(object? sender, EventArgs e) =>
        await _viewModel.ConfirmResetAsync();

    private void DeclineResetClicked(object? sender, EventArgs e) =>
        _viewModel.DeclineReset();

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
                Aurora.Flash(Crimson);
                HideBanner();
                await Task.WhenAll(HideSuccessAsync(), HideAsync(IdentityChip));
                StartScanLine();
                await PunchCameraAsync(1.03);
                break;

            case KioskPhase.Capturing:
            case KioskPhase.Recognized:
                Halo.SetMode(HaloMode.Capturing);
                HideBanner();
                StartScanLine();
                await ShowIdentityAsync();
                _ = FlashShutterWhenCapturedAsync(motion);
                break;

            case KioskPhase.ConfirmReset:
                Halo.SetMode(HaloMode.Capturing);
                HideBanner();
                StopScanLine();
                await ShowIdentityAsync();
                break;

            case KioskPhase.Success:
                Halo.SetMode(HaloMode.Success);
                Aurora.Flash(Green);
                HideBanner();
                StopScanLine();
                PaintCameraRing(Green);
                await HideAsync(IdentityChip);
                if (motion != _motion)
                    return;
                await ShowSuccessAsync();
                break;

            case KioskPhase.Unknown:
                Halo.SetMode(HaloMode.Error);
                Aurora.Flash(Alarm);
                StopScanLine();
                PaintCameraRing(Alarm);
                await Task.WhenAll(HideSuccessAsync(), HideAsync(IdentityChip));
                if (motion != _motion)
                    return;
                await Task.WhenAll(ShowBannerAsync(), ShakeAsync(CameraFrame));
                _ = HideBannerLaterAsync(motion);
                break;

            default:
                Halo.SetMode(HaloMode.Idle);
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

    // ------------------------------------------------------------------ Scanner sizing and scan line

    private void OnScannerSizeChanged(object? sender, EventArgs e)
    {
        var width = ScannerArea.Width;
        var height = ScannerArea.Height;
        if (width <= 0 || height <= 0)
            return;

        var diameter = Math.Clamp(Math.Min(height * 0.7, width * 0.46), 200, 460);
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
                Content = new HorizontalStackLayout { Spacing = 8, Children = { dot, label } },
            };
            _steps.Add((pill, dot, label));
            Stepper.Children.Add(pill);
        }
    }

    private void PaintPhase(KioskPhase phase, KioskPhase previous = KioskPhase.Idle)
    {
        var (text, color) = phase switch
        {
            KioskPhase.Reading => ("Reading badge", Crimson),
            KioskPhase.Capturing or KioskPhase.Recognized => ("Capturing photo", Crimson),
            KioskPhase.ConfirmReset => ("Confirm reset", Crimson),
            KioskPhase.Success => ("Punch saved", Green),
            KioskPhase.Unknown => ("Needs attention", Alarm),
            _ => ("Ready", Crimson),
        };
        // Solid fill with white text: red text on a faint red tint is unreadable on the kiosk screen.
        PhaseLabel.Text = text;
        PhaseLabel.TextColor = Colors.White;
        PhaseDot.BackgroundColor = Colors.White;
        PhaseChip.BackgroundColor = color;
        PhaseChip.Stroke = color;
        PhaseDot.AbortAnimation("phase-dot");
        PhaseDot.Opacity = 1;
        if (phase is KioskPhase.Idle or KioskPhase.Reading or KioskPhase.Capturing or KioskPhase.Recognized or KioskPhase.ConfirmReset)
            LoopPulse(PhaseDot, "phase-dot", phase == KioskPhase.Idle ? 1600u : 600u, 1, 0.25);

        // Which step is active, and where a failure happened.
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
        if (failed >= 0)
            active = failed;

        for (var i = 0; i < _steps.Count; i++)
        {
            var (pill, dot, label) = _steps[i];
            dot.AbortAnimation("step");
            dot.Opacity = 1;
            Color ink;
            if (i == failed)
                ink = Alarm;
            else if (i < active)
                ink = Green;
            else if (i == active)
                ink = Crimson;
            else
                ink = Resource("Faint", Colors.Gray);

            var lit = i <= active || i == failed;
            pill.BackgroundColor = lit ? ink : Colors.Transparent;
            pill.Stroke = lit ? ink : Resource("Line", Colors.Gray);
            dot.BackgroundColor = lit ? Colors.White : ink;
            label.TextColor = lit ? Colors.White : Resource("Faint", Colors.Gray);
            if (i == active && i != failed && phase != KioskPhase.Success)
                LoopPulse(dot, "step", 700, 1, 0.2);

            if (i > 0)
            {
                var link = _links[i - 1];
                link.Color = i <= active && failed < 0 || i < failed ? Green : Resource("Line", Colors.Gray);
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
            ServerLink.Online => Green,
            ServerLink.Offline => Alarm,
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
        if (ClockCamera.StatusText == "Camera live")
            FocusBadge();
    }

    private void PaintCamera()
    {
        var live = ClockCamera.StatusText == "Camera live";
        CameraFallback.Text = ClockCamera.StatusText;
        CameraFallback.IsVisible = !live;
        CameraChipLabel.Text = ClockCamera.StatusText;
        CameraDot.BackgroundColor = live ? Green : Slate;
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
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        Aurora.RefreshTheme();
        Halo.RefreshTheme();
        PaintTheme(animate: false);
        PaintPhase(_viewModel.Phase);
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

        // An animated shadow re-blurs every frame, so the logo glow is a full-motion extra only.
        if (MotionSettings.Level == MotionLevel.Full && LogoTile.Shadow is Shadow glow)
        {
            var animation = new Animation();
            animation.Add(0, 0.5, new Animation(v => glow.Opacity = (float)v, 0.2, 0.7, Easing.SinInOut));
            animation.Add(0.5, 1, new Animation(v => glow.Opacity = (float)v, 0.7, 0.2, Easing.SinInOut));
            animation.Commit(LogoTile, "logo-glow", length: 3200, repeat: () => true);
        }
    }

    private void StopAmbient()
    {
        _ambientStarted = false;
        LiveRipple.AbortAnimation("live");
        FeedRipple.AbortAnimation("feed");
        EmptyRingOuter.AbortAnimation("empty-outer");
        EmptyRingInner.AbortAnimation("empty-inner");
        LogoTile.AbortAnimation("logo-glow");
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
