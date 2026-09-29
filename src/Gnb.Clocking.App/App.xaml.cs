using Gnb.Clocking.App.Pages;
using Gnb.Clocking.App.Theming;

namespace Gnb.Clocking.App;

public partial class App : Microsoft.Maui.Controls.Application
{
	public App()
	{
		InitializeComponent();
		AppearanceSettings.Apply();
		RequestedThemeChanged += (_, _) => AppearanceSettings.RefreshColors();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var services = IPlatformApplication.Current?.Services
			?? throw new InvalidOperationException("The clocking services are not available.");

		// An old 1600x769 laptop cannot hold a 1360x860 window: the bottom of the page falls off the
		// screen, and the page then lays itself out for a height the display never had. Fit the display.
		var display = DeviceDisplay.Current.MainDisplayInfo;
		var density = display.Density > 0 ? display.Density : 1;
		var usableWidth = display.Width / density;
		var usableHeight = display.Height / density;

		// Leave room for the title bar, dock and taskbar rather than filling the panel edge to edge.
		var width = usableWidth > 0 ? Math.Min(1360, usableWidth - 40) : 1360;
		var height = usableHeight > 0 ? Math.Min(860, usableHeight - 80) : 860;

		return new Window(services.GetRequiredService<KioskPage>())
		{
			Title = "GroupNB Clock",
			// Small enough that a short panel can honour it; the layout tightens instead of clipping.
			MinimumWidth = 900,
			MinimumHeight = 560,
			Width = Math.Max(900, width),
			Height = Math.Max(560, height)
		};
	}
}
