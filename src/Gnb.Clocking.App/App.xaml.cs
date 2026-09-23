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

		return new Window(services.GetRequiredService<KioskPage>())
		{
			Title = "GroupNB Clock",
			MinimumWidth = 1100,
			MinimumHeight = 720,
			Width = 1360,
			Height = 860
		};
	}
}
