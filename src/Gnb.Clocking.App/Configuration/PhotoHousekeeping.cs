using Gnb.Clocking.Infrastructure.Photos;
using Microsoft.Extensions.Configuration;

namespace Gnb.Clocking.App.Configuration;

/// <summary>
/// Deletes local clock-photo copies older than <c>ClockKiosk:KeepLocalPhotosDays</c> (default 14) at start-up
/// and then daily, on a background thread, so a 256 GB kiosk disk never fills. The server keeps every photo.
/// </summary>
public static class PhotoHousekeeping
{
    public static void Start(IConfiguration configuration, string photoRoot)
    {
        var keepDays = int.TryParse(configuration["ClockKiosk:KeepLocalPhotosDays"], out var days)
            ? days
            : LocalPhotoPruner.DefaultKeepDays;
        if (keepDays < 1)
            return;

        _ = Task.Run(async () =>
        {
            // Let the window, camera and first sync settle before touching the disk.
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            using var daily = new PeriodicTimer(TimeSpan.FromHours(24));
            do
            {
                try
                {
                    LocalPhotoPruner.Prune(photoRoot, keepDays, DateTime.Today);
                }
                catch (Exception)
                {
                    // Housekeeping must never take the kiosk down.
                }
            }
            while (await daily.WaitForNextTickAsync().ConfigureAwait(false));
        });
    }
}
