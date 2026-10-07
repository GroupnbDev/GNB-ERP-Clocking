using Gnb.Clocking.App.Camera;
using Gnb.Clocking.App.Configuration;
using Gnb.Clocking.App.Controls;
using Gnb.Clocking.App.Diagnostics;
using Gnb.Clocking.App.Pages;
using Gnb.Clocking.App.Photos;
using Gnb.Clocking.App.ViewModels;
using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
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
			})
			.ConfigureLifecycleEvents(events =>
			{
#if WINDOWS
				events.AddWindows(windows => windows.OnWindowCreated(window =>
				{
					KioskStayAwake.Hold();
					FullScreenToggle.Attach(window);
					BadgeEntrySetup.AttachWindow(window);
				}));
#endif
			});

		BadgeEntrySetup.Configure();

		var kiosk = KioskConfiguration.Load(builder.Configuration, FileSystem.Current.AppDataDirectory);
		var camera = CameraSettings.Load(builder.Configuration);
		CameraSettingsAccess.StallMilliseconds = camera.StallMilliseconds;
		CameraSettingsAccess.MaxPhotoAgeMs = camera.MaxPhotoAgeMs;
		MotionSettings.Configure(builder.Configuration["ClockKiosk:Motion"]);
		var logDirectory = Path.Combine(kiosk.PhotoRoot, "logs");
		builder.Logging.AddProvider(new FileLoggerProvider(logDirectory));
#if DEBUG
		builder.Logging.AddDebug();
#endif
		PhotoHousekeeping.Start(builder.Configuration, kiosk.PhotoRoot, logDirectory, camera.LogRetentionDays);
		ClockPhotoThumbnail.Write = MauiPhotoThumbnail.Write;
		builder.Services.AddSingleton(camera);
		builder.Services.AddGroupNbClocking(kiosk);
		builder.Services.AddSingleton<MauiClockCamera>();
		builder.Services.AddSingleton<IClockCamera>(services => services.GetRequiredService<MauiClockCamera>());
		builder.Services.AddSingleton<KioskViewModel>();
		builder.Services.AddSingleton<KioskPage>();

		var app = builder.Build();
		KioskLog.Factory = app.Services.GetRequiredService<ILoggerFactory>();
		var log = KioskLog.Create<MauiApp>();
		log.LogInformation(
			"App start version {Version} motion {Motion} reason {Reason} camera preview format pending",
			AppInfo.Current.VersionString,
			MotionSettings.Level,
			MotionSettings.Reason);
		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
		{
			if (args.ExceptionObject is Exception ex)
				log.LogCritical(ex, "Unhandled exception");
			else
				log.LogCritical("Unhandled exception {Error}", args.ExceptionObject);
		};
		TaskScheduler.UnobservedTaskException += (_, args) =>
		{
			log.LogError(args.Exception, "Unobserved task exception");
			args.SetObserved();
		};
		return app;
	}
}
