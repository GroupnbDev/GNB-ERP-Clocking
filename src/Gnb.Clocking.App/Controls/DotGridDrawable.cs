namespace Gnb.Clocking.App.Controls;

/// <summary>
/// The faint background dot grid. Drawn once and only repainted on resize or theme change;
/// <see cref="AuroraDrawable"/> animates the few dots under its moving spotlight on top.
/// </summary>
public sealed class DotGridDrawable : IDrawable
{
    public const float Spacing = 32;

    public void Draw(ICanvas canvas, RectF rect)
    {
        var dark = Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Dark;
        canvas.FillColor = (dark ? Colors.White : Color.FromArgb("#1A202C")).WithAlpha(0.045f);
        for (var y = Spacing / 2; y < rect.Height; y += Spacing)
        for (var x = Spacing / 2; x < rect.Width; x += Spacing)
            canvas.FillCircle(x, y, 1.1f);
    }
}
