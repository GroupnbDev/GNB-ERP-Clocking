using System.Diagnostics;
using Microsoft.Maui.Controls.Shapes;

namespace Gnb.Clocking.App.Controls;

public enum HaloMode
{
    Idle,
    Reading,
    Capturing,
    Success,
    Error
}

/// <summary>
/// Colours are limited to the groupnb.ca palette (reds #AB1100 / #C70000, the Kadence grays, green #28A745).
/// The rings around the kiosk camera, built as layers so the GPU does the moving:
/// <list type="bullet">
/// <item>the glow, the arcs and the dashed ring are drawn once, then only faded or rotated (layer transforms, no redraw);</item>
/// <item>the seconds bezel redraws once a second;</item>
/// <item>sparks, the capture ring and shockwaves draw frame by frame only during a scan, then that layer sleeps.</item>
/// </list>
/// An idle kiosk therefore redraws nothing but one bezel frame per second.
/// </summary>
public sealed class ScannerHalo : Grid
{
    public static readonly Color Crimson = Color.FromArgb("#C70000");
    public static readonly Color Red = Color.FromArgb("#AB1100");
    public static readonly Color Green = Color.FromArgb("#28A745");
    public static readonly Color Alarm = Color.FromArgb("#C70000");

    /// <summary>Matches the view model's pause between showing the badge and taking the photo.</summary>
    public const double CaptureSeconds = 0.7;

    private const double GlowTile = 200;

    private readonly Border _glow = new()
    {
        WidthRequest = GlowTile,
        HeightRequest = GlowTile,
        StrokeThickness = 0,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
        InputTransparent = true,
    };

    private readonly BezelDrawable _bezelDrawable = new();
    private readonly ArcsDrawable _arcsDrawable = new();
    private readonly DashesDrawable _dashesDrawable = new();
    private readonly HaloEffects _effectsDrawable = new();
    private readonly GraphicsView _bezel;
    private readonly GraphicsView _arcs;
    private readonly GraphicsView _dashes;
    private readonly AnimatedCanvas _effects = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private IDispatcherTimer? _secondTimer;
    private IDispatcherTimer? _liteSpin;
    private Color _accent = Crimson;
    private double _last;
    private double _spin;
    private double _speed = 16;
    private double _targetSpeed = 16;
    private int _second = -1;
    private bool _running;

    public ScannerHalo()
    {
        InputTransparent = true;
        _glow.StrokeShape = new RoundRectangle { CornerRadius = GlowTile / 2 };
        _bezel = Layer(_bezelDrawable);
        _arcs = Layer(_arcsDrawable);
        _dashes = Layer(_dashesDrawable);
        _effects.Scene = _effectsDrawable;
        _effects.FrameInterval = TimeSpan.FromMilliseconds(MotionSettings.IsLite ? 33 : 16);

        Children.Add(_glow);
        Children.Add(_bezel);
        Children.Add(_dashes);
        Children.Add(_arcs);
        Children.Add(_effects);

        Loaded += (_, _) => Resume();
        Unloaded += (_, _) => Pause();
        PaintAccent();
    }

    public void Resize(double cameraDiameter)
    {
        var size = cameraDiameter * 1.95;
        WidthRequest = size;
        HeightRequest = size;
        _glow.Scale = size / GlowTile;

        var radius = (float)(cameraDiameter / 2);
        _bezelDrawable.CoreRadius = radius;
        _arcsDrawable.CoreRadius = radius;
        _dashesDrawable.CoreRadius = radius;
        _effectsDrawable.CoreRadius = radius;
        RedrawLayers();
    }

    public void SetMode(HaloMode mode)
    {
        _effects.FrameInterval = TimeSpan.FromMilliseconds(MotionSettings.IsLite ? 33 : 16);
        _effectsDrawable.SetMode(mode);
        _effects.Wake();
        _targetSpeed = mode switch
        {
            HaloMode.Reading => 240,
            HaloMode.Capturing => 110,
            HaloMode.Success => 50,
            HaloMode.Error => 6,
            _ => 16
        };

        var accent = mode switch
        {
            HaloMode.Success => Green,
            HaloMode.Error => Alarm,
            _ => Crimson
        };
        if (!accent.Equals(_accent))
        {
            _accent = accent;
            PaintAccent();
        }
    }

    public void RefreshTheme() => RedrawLayers();

    public void Pause()
    {
        _running = false;
        this.AbortAnimation("halo-spin");
        _glow.AbortAnimation("halo-breathe");
        _secondTimer?.Stop();
        _secondTimer = null;
        _liteSpin?.Stop();
        _liteSpin = null;
        _effects.Pause();
    }

    public void Resume()
    {
        if (_running)
            return;

        _running = true;
        _effects.Resume();
        UpdateSecond();
        _secondTimer = Dispatcher.CreateTimer();
        _secondTimer.Interval = TimeSpan.FromMilliseconds(250);
        _secondTimer.Tick += (_, _) => UpdateSecond();
        _secondTimer.Start();

        if (MotionSettings.Level == MotionLevel.Off)
            return;

        _last = _watch.Elapsed.TotalSeconds;
        if (MotionSettings.IsLite)
        {
            // Lite: a plain 20 Hz timer instead of the display-rate animation ticker, and no breathing glow.
            _liteSpin = Dispatcher.CreateTimer();
            _liteSpin.Interval = TimeSpan.FromMilliseconds(50);
            _liteSpin.Tick += (_, _) => Spin();
            _liteSpin.Start();
            return;
        }

        new Animation(_ => Spin(), 0, 1).Commit(this, "halo-spin", length: 1000, repeat: () => _running);

        new Animation
        {
            { 0, 0.5, new Animation(v => _glow.Opacity = v, 0.6, 1, Easing.SinInOut) },
            { 0.5, 1, new Animation(v => _glow.Opacity = v, 1, 0.6, Easing.SinInOut) },
        }.Commit(_glow, "halo-breathe", length: 3600, repeat: () => _running);
    }

    private void Spin()
    {
        var now = _watch.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _last, 0, 0.1);

        _last = now;
        _speed += (_targetSpeed - _speed) * Math.Min(1, dt * 3);
        _spin = (_spin + _speed * dt) % 360;

        // Rotation is a layer transform: the arcs are not redrawn.
        _arcs.Rotation = _spin;
        _dashes.Rotation = -_spin * 0.5;
        _effectsDrawable.Spin = (float)_spin;
    }

    private void UpdateSecond()
    {
        var second = DateTime.Now.Second;
        if (second == _second)
            return;

        _second = second;
        _bezelDrawable.Second = second;
        _bezel.Invalidate();
    }

    private void PaintAccent()
    {
        _bezelDrawable.Accent = _accent;
        _arcsDrawable.Accent = _accent;
        _effectsDrawable.Accent = _accent;
        _glow.Background = new RadialGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(_accent.WithAlpha(0f), 0f),
                new GradientStop(_accent.WithAlpha(0.34f), 0.5f),
                new GradientStop(_accent.WithAlpha(0f), 1f),
            },
            new Point(0.5, 0.5),
            0.5);
        RedrawLayers();
    }

    private void RedrawLayers()
    {
        _bezel.Invalidate();
        _arcs.Invalidate();
        _dashes.Invalidate();
        _effects.Wake();
    }

    private static GraphicsView Layer(IDrawable drawable) => new()
    {
        Drawable = drawable,
        BackgroundColor = Colors.Transparent,
        InputTransparent = true,
    };

    private static Color Neutral =>
        Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Dark
            ? Colors.White
            : Color.FromArgb("#1A202C");

    /// <summary>Draws an arc as short chords whose opacity ramps from tail to head.</summary>
    private static void DrawArc(ICanvas canvas, PointF c, float radius, float startDeg, float sweepDeg,
        Color color, float tailAlpha, float headAlpha)
    {
        const float step = 4f;
        var steps = Math.Max(1, (int)MathF.Ceiling(sweepDeg / step));
        canvas.StrokeLineCap = LineCap.Butt;
        for (var j = 0; j < steps; j++)
        {
            var a0 = (startDeg + j * sweepDeg / steps) * MathF.PI / 180;
            var a1 = (startDeg + (j + 1) * sweepDeg / steps) * MathF.PI / 180;
            var k = (j + 1f) / steps;
            canvas.StrokeColor = color.WithAlpha(tailAlpha + (headAlpha - tailAlpha) * k);
            canvas.DrawLine(
                c.X + MathF.Cos(a0) * radius, c.Y + MathF.Sin(a0) * radius,
                c.X + MathF.Cos(a1) * radius, c.Y + MathF.Sin(a1) * radius);
        }

        canvas.StrokeLineCap = LineCap.Round;
    }

    private static void Dot(ICanvas canvas, PointF c, float radius, float angleDeg, float size, Color color, float glow)
    {
        var a = angleDeg * MathF.PI / 180;
        canvas.SaveState();
        if (glow > 0)
            canvas.SetShadow(SizeF.Zero, glow, color);
        canvas.FillColor = color;
        canvas.FillCircle(c.X + MathF.Cos(a) * radius, c.Y + MathF.Sin(a) * radius, size);
        canvas.RestoreState();
    }

    /// <summary>60 ticks; the ones already passed this minute take the accent. Redrawn once a second.</summary>
    private sealed class BezelDrawable : IDrawable
    {
        public float CoreRadius { get; set; } = 150;
        public Color Accent { get; set; } = Crimson;
        public int Second { get; set; }

        public void Draw(ICanvas canvas, RectF rect)
        {
            var c = rect.Center;
            var inner = CoreRadius + 16;
            var neutral = Neutral;
            canvas.StrokeLineCap = LineCap.Round;

            for (var i = 0; i < 60; i++)
            {
                if (i == Second)
                    continue;

                var major = i % 5 == 0;
                canvas.StrokeSize = major ? 2 : 1.4f;
                canvas.StrokeColor = i < Second
                    ? Accent.WithAlpha(major ? 0.7f : 0.45f)
                    : neutral.WithAlpha(major ? 0.28f : 0.13f);
                Tick(canvas, c, inner, i, major ? 9f : 5f);
            }

            canvas.SaveState();
            canvas.SetShadow(SizeF.Zero, 10, Accent);
            canvas.StrokeSize = 3;
            canvas.StrokeColor = Accent;
            Tick(canvas, c, inner, Second, 14f);
            canvas.RestoreState();
        }

        private static void Tick(ICanvas canvas, PointF c, float inner, int index, float length)
        {
            var angle = (index * 6 - 90) * MathF.PI / 180;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            canvas.DrawLine(c.X + cos * inner, c.Y + sin * inner, c.X + cos * (inner + length), c.Y + sin * (inner + length));
        }
    }

    /// <summary>Three fading arcs and a glowing orbiter. Drawn once per colour; the layer is rotated.</summary>
    private sealed class ArcsDrawable : IDrawable
    {
        public float CoreRadius { get; set; } = 150;
        public Color Accent { get; set; } = Crimson;

        public void Draw(ICanvas canvas, RectF rect)
        {
            var c = rect.Center;
            var ring = CoreRadius + 46;
            canvas.StrokeSize = 3;
            for (var k = 0; k < 3; k++)
                DrawArc(canvas, c, ring, k * 120, 54, Accent, 0.05f, 0.9f);

            Dot(canvas, c, ring, 70, 4.5f, Accent, 12);
        }
    }

    /// <summary>The dashed outer ring and its two dots. Drawn once per theme; the layer is counter-rotated.</summary>
    private sealed class DashesDrawable : IDrawable
    {
        public float CoreRadius { get; set; } = 150;

        public void Draw(ICanvas canvas, RectF rect)
        {
            var c = rect.Center;
            var neutral = Neutral;
            var radius = CoreRadius + 66;
            canvas.StrokeSize = 1.5f;
            canvas.StrokeDashPattern = new float[] { 2, 7 };
            canvas.StrokeColor = neutral.WithAlpha(0.2f);
            canvas.DrawCircle(c.X, c.Y, radius);
            canvas.StrokeDashPattern = null;

            Dot(canvas, c, radius, 200, 3.5f, Red, 8);
            Dot(canvas, c, radius, 20, 2.5f, neutral.WithAlpha(0.6f), 0);
        }
    }

    /// <summary>Per-frame scan effects. Reports <see cref="IsAnimating"/> false once settled so its canvas sleeps.</summary>
    private sealed class HaloEffects : IAnimatedScene
    {
        private const int MaxSparks = 160;

        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly List<Spark> _sparks = new();
        private readonly Random _random = new();
        private HaloMode _mode = HaloMode.Idle;
        private double _modeAt;
        private double _last;
        private double _spawnDebt;

        public float CoreRadius { get; set; } = 150;
        public Color Accent { get; set; } = Crimson;
        public float Spin { get; set; }

        public bool IsAnimating =>
            _sparks.Count > 0
            || _mode is HaloMode.Reading or HaloMode.Capturing
            || (_mode is HaloMode.Success or HaloMode.Error && Age < 1.6);

        private double Age => _watch.Elapsed.TotalSeconds - _modeAt;

        public void SetMode(HaloMode mode)
        {
            if (mode == _mode)
                return;

            _mode = mode;
            _modeAt = _watch.Elapsed.TotalSeconds;
            if (mode == HaloMode.Success)
                Burst(Green, 46, 120, 340);
            else if (mode == HaloMode.Error)
                Burst(Alarm, 24, 80, 220);
        }

        public void Draw(ICanvas canvas, RectF rect)
        {
            var now = _watch.Elapsed.TotalSeconds;
            var dt = (float)Math.Clamp(now - _last, 0, 0.1);
            _last = now;

            // With motion off every effect is drawn in its finished state, in one frame.
            var settled = MotionSettings.Level == MotionLevel.Off;
            var age = settled ? 10 : now - _modeAt;
            var c = rect.Center;
            var r = CoreRadius;

            switch (_mode)
            {
                case HaloMode.Reading when !settled:
                {
                    var head = Spin * 1.8f;
                    canvas.StrokeSize = 4;
                    DrawArc(canvas, c, r + 31, head - 80, 80, Accent, 0f, 1f);
                    DrawArc(canvas, c, r + 31, head + 100, 80, Accent, 0f, 0.6f);
                    break;
                }
                case HaloMode.Capturing:
                {
                    var progress = (float)Math.Min(1, age / CaptureSeconds);
                    canvas.StrokeSize = 5;
                    if (progress > 0.01f)
                        DrawArc(canvas, c, r + 7, -90, 360 * progress, Accent, 1f, 1f);

                    var squeeze = (float)Math.Min(1, age / 0.55);
                    var eased = 1 - (1 - squeeze) * (1 - squeeze);
                    canvas.StrokeSize = 2;
                    canvas.StrokeColor = Accent.WithAlpha(squeeze * (1 - squeeze) * 2.6f);
                    canvas.DrawCircle(c.X, c.Y, r + 110 - 100 * eased);
                    break;
                }
                case HaloMode.Success:
                    canvas.StrokeSize = 5;
                    canvas.StrokeColor = Accent.WithAlpha((float)Math.Min(1, age * 4));
                    canvas.DrawCircle(c.X, c.Y, r + 7);
                    Shockwaves(canvas, c, r, age, 3);
                    break;

                case HaloMode.Error:
                    canvas.StrokeSize = 4;
                    canvas.StrokeDashPattern = new float[] { 6, 8 };
                    canvas.StrokeColor = Accent.WithAlpha(0.9f);
                    canvas.DrawCircle(c.X, c.Y, r + 7);
                    canvas.StrokeDashPattern = null;
                    Shockwaves(canvas, c, r, age, 1);
                    break;
            }

            if (!settled)
                UpdateSparks(canvas, c, r, dt);
        }

        private void Shockwaves(ICanvas canvas, PointF c, float r, double age, int count)
        {
            for (var k = 0; k < count; k++)
            {
                var p = (float)((age - k * 0.2) / 1.2);
                if (p <= 0 || p >= 1)
                    continue;

                canvas.StrokeSize = 3 * (1 - p) + 0.5f;
                canvas.StrokeColor = Accent.WithAlpha((1 - p) * 0.8f);
                canvas.DrawCircle(c.X, c.Y, r + 8 + p * r * 0.9f);
            }
        }

        private void UpdateSparks(ICanvas canvas, PointF c, float r, float dt)
        {
            var inward = _mode is HaloMode.Reading or HaloMode.Capturing;
            var rate = !inward ? 0 : (MotionSettings.IsLite ? 0.4 : 1.0) * (_mode == HaloMode.Reading ? 60 : 32);
            _spawnDebt += rate * dt;
            while (_spawnDebt >= 1 && _sparks.Count < MaxSparks)
            {
                _spawnDebt--;
                var angle = _random.NextDouble() * Math.PI * 2;
                var distance = r + 90 + _random.NextSingle() * 70;
                var speed = -(90 + _random.NextSingle() * 90);
                _sparks.Add(new Spark
                {
                    X = (float)Math.Cos(angle) * distance,
                    Y = (float)Math.Sin(angle) * distance,
                    VX = (float)Math.Cos(angle) * speed,
                    VY = (float)Math.Sin(angle) * speed,
                    Life = 1.6f,
                    Size = 1.2f + _random.NextSingle() * 1.8f,
                    Inward = true,
                });
            }

            if (_spawnDebt > 1)
                _spawnDebt = 0;

            for (var i = _sparks.Count - 1; i >= 0; i--)
            {
                var s = _sparks[i];
                s.Age += dt;
                s.X += s.VX * dt;
                s.Y += s.VY * dt;
                if (!s.Inward)
                {
                    s.VX *= 1 - Math.Min(1, 1.4f * dt);
                    s.VY *= 1 - Math.Min(1, 1.4f * dt);
                }

                if (s.Age >= s.Life || (s.Inward && s.X * s.X + s.Y * s.Y < (r + 8) * (r + 8)))
                {
                    _sparks.RemoveAt(i);
                    continue;
                }

                var fade = MathF.Sin(MathF.PI * s.Age / s.Life);
                canvas.FillColor = (s.Color ?? Accent).WithAlpha(0.85f * fade);
                canvas.FillCircle(c.X + s.X, c.Y + s.Y, s.Size);
            }
        }

        private void Burst(Color color, int count, float minSpeed, float maxSpeed)
        {
            if (MotionSettings.Level == MotionLevel.Off)
                return;
            if (MotionSettings.IsLite)
                count /= 2;

            var r = CoreRadius + 8;
            for (var i = 0; i < count && _sparks.Count < MaxSparks; i++)
            {
                var angle = _random.NextDouble() * Math.PI * 2;
                var speed = minSpeed + _random.NextSingle() * (maxSpeed - minSpeed);
                _sparks.Add(new Spark
                {
                    X = (float)Math.Cos(angle) * r,
                    Y = (float)Math.Sin(angle) * r,
                    VX = (float)Math.Cos(angle) * speed,
                    VY = (float)Math.Sin(angle) * speed,
                    Life = 0.8f + _random.NextSingle() * 0.7f,
                    Size = 2f + _random.NextSingle() * 2.2f,
                    Color = color,
                });
            }
        }

        private sealed class Spark
        {
            public float X;
            public float Y;
            public float VX;
            public float VY;
            public float Age;
            public float Life;
            public float Size;
            public bool Inward;
            public Color? Color;
        }
    }
}
