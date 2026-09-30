namespace Gnb.Clocking.App.Theming;

/// <summary>
/// Status colours for code that paints at run time (halo, aurora, phase chip, stepper). Reads the current theme's
/// tokens from app resources (<see cref="AppearanceSettings.RefreshColors"/>), so call it when painting, not once.
/// Brand reds stay identity only; these are the only Success / Warning / Error / Neutral colours.
/// </summary>
public static class StatusColors
{
    public static Color SuccessSolid => Get("SuccessSolid", "#15803D");
    public static Color SuccessOnSolid => Get("SuccessOnSolid", "#FFFFFF");
    public static Color WarningSolid => Get("WarningSolid", "#F59E0B");
    public static Color WarningOnSolid => Get("WarningOnSolid", "#1A202C");
    public static Color WarningSoft => Get("WarningSoft", "#FFFBEB");
    public static Color WarningBorder => Get("WarningBorder", "#FCD34D");
    public static Color WarningText => Get("WarningText", "#B45309");
    public static Color ErrorSolid => Get("ErrorSolid", "#DC2626");
    public static Color ErrorOnSolid => Get("ErrorOnSolid", "#FFFFFF");
    public static Color NeutralSolid => Get("NeutralSolid", "#475569");
    public static Color NeutralOnSolid => Get("NeutralOnSolid", "#FFFFFF");

    /// <summary>Brand identity red. Not a status: clock-in rows only.</summary>
    public static readonly Color Brand = Color.FromArgb("#AB1100");

    private static Color Get(string key, string fallback) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Color color
            ? color
            : Color.FromArgb(fallback);
}
