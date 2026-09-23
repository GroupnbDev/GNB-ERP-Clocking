using Gnb.Clocking.App.Camera;
using Gnb.Clocking.App.Configuration;
using Gnb.Clocking.App.Controls;
using Gnb.Clocking.App.Pages;
using Gnb.Clocking.App.ViewModels;
using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Infrastructure;
using Microsoft.Extensions.Logging;
#if MACCATALYST
using Gnb.Clocking.App.Platforms.MacCatalyst;
#elif WINDOWS
using Gnb.Clocking.App.Platforms.Windows;
#endif

namespace Gnb.Clocking.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			})
			.ConfigureMauiHandlers(handlers =>
			{
#if MACCATALYST
				handlers.AddHandler<ClockCameraView, MacClockCameraHandler>();
#elif WINDOWS
				handlers.AddHandler<ClockCameraView, WinClockCameraHandler>();
#endif
			});

		BadgeEntrySetup.Configure();

		var kiosk = KioskConfiguration.Load(builder.Configuration, FileSystem.Current.AppDataDirectory);
		MotionSettings.Configure(builder.Configuration["ClockKiosk:Motion"]);
		PhotoHousekeeping.Start(builder.Configuration, kiosk.PhotoRoot);
		builder.Services.AddGroupNbClocking(kiosk);
		builder.Services.AddSingleton<MauiClockCamera>();
		builder.Services.AddSingleton<IClockCamera>(services => services.GetRequiredService<MauiClockCamera>());
		builder.Services.AddSingleton<KioskViewModel>();
		builder.Services.AddSingleton<KioskPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
