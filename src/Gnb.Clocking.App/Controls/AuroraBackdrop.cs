using System.Diagnostics;

namespace Gnb.Clocking.App.Controls;

/// <summary>
/// Full-window backdrop: a static dot grid, drifting brand glows, a soft moving light and a flash on scan events.
/// Nothing here is redrawn per frame. Each glow is a small gradient tile rendered once and then scaled up and
/// moved with layer transforms, which the GPU composites for almost nothing. The dot grid redraws only on
/// resize or theme change.
/// </summary>
public sealed class AuroraBackdrop : Grid
{
    /// <summary>Glows are rendered at this size and scaled up; a soft gradient loses nothing when stretched.</summary>
    private const double TileSize = 160;

    private static readonly BlobSpec[] Specs =
    {
        new("#AB1100", 0.16, 0.24, 0.10, 0.08, 0.11, 0.0, 0.55, 0.30f),
        new("#C70000", 0.84, 0.78, 0.09, 0.10, 0.09, 1.7, 0.50, 0.22f),
        new("#AB1100", 0.60, 0.10, 0.12, 0.06, 0.07, 3.1, 0.42, 0.12f),
        new("#4A5568", 0.30, 0.94, 0.10, 0.07, 0.06, 4.4, 0.45, 0.10f),
    };

    private readonly GraphicsView _grid = new() { Drawable = new DotGridDrawable(), BackgroundColor = Colors.Transparent, InputTransparent = true };
    private readonly List<(Border Tile, BlobSpec Spec)> _blobs = new();
    private readonly Border _light = Tile();
    private readonly Border _flash = Tile();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private IDispatcherTimer? _liteDrift;
    private double _lastFrame;
    private bool _running;

    public AuroraBackdrop()
    {
        InputTransparent = true;
        Children.Add(_grid);
        foreach (var spec in Specs)
        {
            var tile = Tile();
            _blobs.Add((tile, spec));
            Children.Add(tile);
        }

        Children.Add(_light);
        _flash.Opacity = 0;
        Children.Add(_flash);

        SizeChanged += (_, _) => Place();
        Loaded += (_, _) => Resume();
        Unloaded += (_, _) => Pause();
        RefreshTheme();
    }

    /// <summary>Where scan flashes bloom, relative to the backdrop (0..1).</summary>
    public Point FlashOrigin { get; set; } = new(0.36, 0.55);

    public void RefreshTheme()
    {
        var dark = Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Dark;
        var intensity = dark ? 1f : 0.55f;
        foreach (var (tile, spec) in _blobs)
            tile.Background = Glow(Color.FromArgb(spec.Color), spec.Alpha * intensity);

        _light.Background = Glow(dark ? Colors.White : Color.FromArgb("#1A202C"), dark ? 0.05f : 0.035f);
        _grid.Invalidate();
    }

    public void Flash(Color color)
    {
        if (Width <= 0 || MotionSettings.Level == MotionLevel.Off)
            return;

        var size = Math.Max(Width, Height);
        _flash.AbortAnimation("flash");
        _flash.Background = Glow(color, 0.3f);
        _flash.TranslationX = Width * FlashOrigin.X - TileSize / 2;
        _flash.TranslationY = Height * FlashOrigin.Y - TileSize / 2;
        var from = size * 0.45 / TileSize;
        var to = size * 1.3 / TileSize;
        new Animation
        {
            { 0, 1, new Animation(v => _flash.Scale = v, from, to, Easing.CubicOut) },
            { 0, 1, new Animation(v => _flash.Opacity = v, 1, 0, Easing.CubicIn) },
        }.Commit(_flash, "flash", length: 1400);
    }

    public void Pause()
    {
        _running = false;
        this.AbortAnimation("drift");
        _liteDrift?.Stop();
        _liteDrift = null;
    }

    public void Resume()
    {
        Place();
        if (_running || MotionSettings.Level == MotionLevel.Off)
            return;

        _running = true;
        if (MotionSettings.IsLite)
        {
            // Lite: a 10 Hz timer instead of the display-rate animation ticker.
            _liteDrift = Dispatcher.CreateTimer();
            _liteDrift.Interval = TimeSpan.FromMilliseconds(100);
            _liteDrift.Tick += (_, _) => Drift(force: true);
            _liteDrift.Start();
            return;
        }

        new Animation(_ => Drift(), 0, 1).Commit(this, "drift", length: 1000, repeat: () => _running);
    }

    private void Place()
    {
        if (Width <= 0 || Height <= 0)
            return;

        var size = Math.Max(Width, Height);
        foreach (var (tile, spec) in _blobs)
            tile.Scale = size * spec.Radius * 2 / TileSize;
        _light.Scale = 560 / TileSize;
        Drift(force: true);
    }

    private void Drift(bool force = false)
    {
        var t = _watch.Elapsed.TotalSeconds;

        // The glows move a few pixels a second; 20 updates a second is already smooth.
        if (!force && t - _lastFrame < 0.05)
            return;

        _lastFrame = t;
        var w = Width;
        var h = Height;
        foreach (var (tile, spec) in _blobs)
        {
            tile.TranslationX = w * (spec.X + spec.DriftX * Math.Sin(t * spec.Speed + spec.Phase)) - TileSize / 2;
            tile.TranslationY = h * (spec.Y + spec.DriftY * Math.Cos(t * spec.Speed * 0.8 + spec.Phase)) - TileSize / 2;
        }

        _light.TranslationX = w * (0.5 + 0.38 * Math.Sin(t * 0.13)) - TileSize / 2;
        _light.TranslationY = h * (0.5 + 0.32 * Math.Cos(t * 0.17)) - TileSize / 2;
    }

    private static Border Tile() => new()
    {
        WidthRequest = TileSize,
        HeightRequest = TileSize,
        StrokeThickness = 0,
        HorizontalOptions = LayoutOptions.Start,
        VerticalOptions = LayoutOptions.Start,
        InputTransparent = true,
        BackgroundColor = Colors.Transparent,
    };

    private static RadialGradientBrush Glow(Color color, float alpha) => new(
        new GradientStopCollection
        {
            new GradientStop(color.WithAlpha(alpha), 0f),
            new GradientStop(color.WithAlpha(alpha * 0.35f), 0.55f),
            new GradientStop(color.WithAlpha(0f), 1f),
        },
        new Point(0.5, 0.5),
        0.5);

    private sealed record BlobSpec(
        string Color, double X, double Y, double DriftX, double DriftY,
        double Speed, double Phase, double Radius, float Alpha);
}
