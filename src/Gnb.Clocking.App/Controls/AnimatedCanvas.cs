using System.Diagnostics;

namespace Gnb.Clocking.App.Controls;

/// <summary>A scene that can tell its canvas when there is nothing left to animate.</summary>
public interface IAnimatedScene : IDrawable
{
    bool IsAnimating { get; }
}

/// <summary>
/// A <see cref="GraphicsView"/> that redraws <see cref="Scene"/> on a frame timer.
/// Drawing runs on the UI thread (the same thread that captures RFID keystrokes), so the canvas keeps
/// itself cheap: it sleeps as soon as an <see cref="IAnimatedScene"/> stops animating, never queues a
/// frame while one is pending, stretches its frame interval when a frame goes over budget, and stops
/// while paused. <see cref="MotionLevel.Off"/> draws single frames only.
/// </summary>
public class AnimatedCanvas : GraphicsView
{
    private const double BudgetMs = 5;
    private const double MaxThrottle = 4;

    private readonly Stopwatch _drawWatch = new();
    private IDispatcherTimer? _timer;
    private IDrawable? _scene;
    private TimeSpan _interval = TimeSpan.FromMilliseconds(16);
    private double _throttle = 1;
    private double _appliedThrottle = 1;
    private double _drawMs;
    private bool _pending;
    private bool _paused;
    private bool _loaded;

    public AnimatedCanvas()
    {
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
        Drawable = new TimedDrawable(this);
        Loaded += (_, _) =>
        {
            _loaded = true;
            Wake();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            StopTimer();
        };
    }

    public IDrawable? Scene
    {
        get => _scene;
        set
        {
            _scene = value;
            Wake();
        }
    }

    /// <summary>Target time between frames before any automatic throttling.</summary>
    public TimeSpan FrameInterval
    {
        get => _interval;
        set
        {
            if (_interval == value)
                return;

            _interval = value;
            ApplyInterval();
        }
    }

    public void Pause()
    {
        _paused = true;
        StopTimer();
    }

    public void Resume()
    {
        _paused = false;
        Wake();
    }

    /// <summary>Draws the next frame and keeps ticking until the scene reports it is done.</summary>
    public void Wake()
    {
        Invalidate();
        if (!_loaded || _paused || _timer != null || MotionSettings.Level == MotionLevel.Off)
            return;

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = _interval * _throttle;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer == null)
            return;

        _timer.Tick -= OnTick;
        _timer.Stop();
        _timer = null;
        _pending = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // The last frame has not been drawn yet: the UI thread is busy, so do not pile on.
        if (_pending)
            return;

        _pending = true;
        Invalidate();
    }

    private void Render(ICanvas canvas, RectF rect)
    {
        _pending = false;
        if (_scene == null)
            return;

        _drawWatch.Restart();
        _scene.Draw(canvas, rect);
        _drawWatch.Stop();

        _drawMs = _drawMs * 0.9 + _drawWatch.Elapsed.TotalMilliseconds * 0.1;
        if (_drawMs > BudgetMs)
            _throttle = Math.Min(MaxThrottle, _throttle * 1.2);
        else if (_drawMs < BudgetMs / 3)
            _throttle = Math.Max(1, _throttle * 0.99);

        if (Math.Abs(_throttle - _appliedThrottle) / _appliedThrottle > 0.1)
            ApplyInterval();

        if (_throttle >= MaxThrottle && _drawMs > BudgetMs)
            MotionSettings.ReportOverBudget();

        // Nothing moving: keep the last frame on screen and stop ticking until woken.
        if (_scene is IAnimatedScene { IsAnimating: false })
            StopTimer();
    }

    private void ApplyInterval()
    {
        _appliedThrottle = _throttle;
        if (_timer != null)
            _timer.Interval = _interval * _throttle;
    }

    private sealed class TimedDrawable(AnimatedCanvas owner) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect) => owner.Render(canvas, dirtyRect);
    }
}
