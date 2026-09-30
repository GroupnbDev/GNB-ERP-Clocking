namespace Gnb.Clocking.App.Theming;

public static class AppearanceSettings
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string System = "System";

    private const string Key = "groupnb.appearance";

    public static string Current => Preferences.Default.Get(Key, System);

    public static void Apply()
    {
        var app = Microsoft.Maui.Controls.Application.Current;
        if (app == null)
            return;

        app.UserAppTheme = ToTheme(Current);
        RefreshColors();
    }

    public static void RefreshColors()
    {
        var app = Microsoft.Maui.Controls.Application.Current;
        if (app == null)
            return;

        var dark = app.RequestedTheme == AppTheme.Dark;
        // Surfaces: groupnb.ca Kadence globals #1A202C…#F7FAFC and greys #D4D4D4 / #98999A.
        Set(app.Resources, "Canvas", dark ? "#1A202C" : "#F7FAFC");
        Set(app.Resources, "Stage", dark ? "#B32D3748" : "#B8FFFFFF");
        Set(app.Resources, "Glass", dark ? "#E62D3748" : "#E6FFFFFF");
        Set(app.Resources, "Ink", dark ? "#F7FAFC" : "#1A202C");
        Set(app.Resources, "Muted", dark ? "#D4D4D4" : "#4A5568");
        Set(app.Resources, "Faint", dark ? "#98999A" : "#718096");
        Set(app.Resources, "Line", dark ? "#1FFFFFFF" : "#1A1A202C");

        // Status palette: Success / Warning / Error / Neutral, separate from the brand reds.
        Set(app.Resources, "LineStrong", dark ? "#33FFFFFF" : "#331A202C");
        Set(app.Resources, "SuccessSolid", dark ? "#16A34A" : "#15803D");
        Set(app.Resources, "SuccessOnSolid", dark ? "#052E16" : "#FFFFFF");
        Set(app.Resources, "SuccessText", dark ? "#4ADE80" : "#166534");
        Set(app.Resources, "WarningSolid", dark ? "#F59E0B" : "#F59E0B");
        Set(app.Resources, "WarningOnSolid", dark ? "#1A202C" : "#1A202C");
        Set(app.Resources, "WarningSoft", dark ? "#2B2112" : "#FFFBEB");
        Set(app.Resources, "WarningBorder", dark ? "#92400E" : "#FCD34D");
        Set(app.Resources, "WarningText", dark ? "#FBBF24" : "#B45309");
        Set(app.Resources, "ErrorSolid", dark ? "#EF4444" : "#DC2626");
        Set(app.Resources, "ErrorOnSolid", dark ? "#FFFFFF" : "#FFFFFF");
        Set(app.Resources, "ErrorSoft", dark ? "#2A1417" : "#FEF2F2");
        Set(app.Resources, "ErrorBorder", dark ? "#7F1D1D" : "#FCA5A5");
        Set(app.Resources, "ErrorText", dark ? "#F87171" : "#B91C1C");
        Set(app.Resources, "NeutralSolid", dark ? "#64748B" : "#475569");
        Set(app.Resources, "NeutralOnSolid", dark ? "#FFFFFF" : "#FFFFFF");
        Set(app.Resources, "NeutralSoft", dark ? "#232C3A" : "#F1F5F9");
        Set(app.Resources, "NeutralBorder", dark ? "#334155" : "#CBD5E1");
    }

    private static void Set(ResourceDictionary resources, string key, string hex) =>
        resources[key] = Color.FromArgb(hex);

    public static void Set(string choice)
    {
        var value = choice is Light or Dark or System ? choice : System;
        Preferences.Default.Set(Key, value);
        Apply();
    }

    private static AppTheme ToTheme(string choice) => choice switch
    {
        Light => AppTheme.Light,
        Dark => AppTheme.Dark,
        _ => AppTheme.Unspecified
    };
}
