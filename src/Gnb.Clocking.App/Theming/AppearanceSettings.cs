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
        // groupnb.ca palette only (Kadence globals #1A202C…#F7FAFC, greys #D4D4D4 / #98999A, reds #AB1100 / #C70000).
        Set(app.Resources, "Canvas", dark ? "#1A202C" : "#F7FAFC");
        Set(app.Resources, "Stage", dark ? "#B32D3748" : "#B8FFFFFF");
        Set(app.Resources, "Glass", dark ? "#E62D3748" : "#E6FFFFFF");
        Set(app.Resources, "Ink", dark ? "#F7FAFC" : "#1A202C");
        Set(app.Resources, "Muted", dark ? "#D4D4D4" : "#4A5568");
        Set(app.Resources, "Faint", dark ? "#98999A" : "#718096");
        Set(app.Resources, "Line", dark ? "#1FFFFFFF" : "#1A1A202C");
        Set(app.Resources, "Danger", "#C70000");
        Set(app.Resources, "DangerSoft", "#C70000");
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
