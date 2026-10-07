using Microsoft.UI.Xaml;
using Gnb.Clocking.App.Diagnostics;
using Gnb.Clocking.App.Platforms.Windows;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Gnb.Clocking.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		if (KioskSupervisor.WatchIfSupervisor())
			return;

		KioskSupervisor.SuppressCrashDialog();
		KioskSupervisor.EnsureWatchingThisProcess();
		this.InitializeComponent();
		UnhandledException += (_, args) =>
		{
			// Mark it handled before logging. WinUI still closes the process if this flag is set
			// after the exception object is left unread, or if logging throws first.
			args.Handled = true;
			var error = args.Exception;
			try
			{
				KioskLog.Create<App>().LogCritical(error, "WinUI unhandled exception");
			}
			catch (Exception)
			{
				// The kiosk stays up even when the log file cannot be written.
			}
		};
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

