using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Infrastructure.ClockKiosk;
using Gnb.Clocking.Infrastructure.ClockKiosk.Offline;
using Gnb.Clocking.Infrastructure.Photos;
using Gnb.Clocking.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace Gnb.Clocking.Infrastructure;

public static class ClockingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the kiosk against gnbSaasApi <c>api/clock-kiosk</c>. The API owns the recruitment
    /// database connection; this app only holds <c>ClockKiosk:BaseUrl</c> and <c>ClockKiosk:ApiKey</c>.
    /// </summary>
    public static IServiceCollection AddGroupNbClocking(this IServiceCollection services, ClockKioskApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<KioskShiftCache>();
        // One SQLite file next to the photos: queued punches, cached roster, measured clock skew.
        services.AddSingleton(new OfflineClockStore(Path.Combine(options.PhotoRoot, "clock-queue.db3")));
        services.AddSingleton<ClockSyncWorker>();
        services.AddSingleton(provider => new ClockKioskApiClient(CreateHttp(), provider.GetRequiredService<ClockKioskApiOptions>()));
        services.AddSingleton(new FileClockPhotoStore(options.PhotoRoot));
        services.AddSingleton<ICandidateBadgeDirectory, HttpCandidateBadgeDirectory>();
        services.AddSingleton<IClockPhotoStore, HttpClockPhotoStore>();
        services.AddSingleton<IClockingService, HttpClockingService>();

        return services;
    }

    /// <summary>
    /// A process that stays up for days must drop pooled sockets. The default handler keeps them
    /// forever, and a dead connection then sits on the next tap until the request times out.
    /// </summary>
    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        return new HttpClient(handler, disposeHandler: true);
    }
}
