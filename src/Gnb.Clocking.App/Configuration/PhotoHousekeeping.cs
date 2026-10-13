using Gnb.Clocking.Infrastructure.Diagnostics;
using Gnb.Clocking.Infrastructure.Photos;
using Microsoft.Extensions.Configuration;

namespace Gnb.Clocking.App.Configuration;

/// <summary>
/// Deletes local clock-photo copies older than <c>ClockKiosk:KeepLocalPhotosDays</c> (default 7) at start-up
/// and then daily, on a background thread. A week of small JPEGs is enough for the on-screen list.
/// </summary>
public static class PhotoHousekeeping
{
    public static void Start(IConfiguration configuration, string photoRoot, string logDirectory, int logRetentionDays)
    {
        var keepDays = int.TryParse(configuration["ClockKiosk:KeepLocalPhotosDays"], out var days)
            ? days
            : LocalPhotoPruner.DefaultKeepDays;

        _ = Task.Run(async () =>
        {
            try
            {
                FileLoggerProvider.Prune(logDirectory, logRetentionDays, DateTime.Today);
            }
            catch (Exception)
            {
            }

            // Let the window, camera and first sync settle before touching the photo disk.
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            using var daily = new PeriodicTimer(TimeSpan.FromHours(24));
            do
            {
                try
                {
                    if (keepDays >= 1)
                        LocalPhotoPruner.Prune(photoRoot, keepDays, DateTime.Today);
                    FileLoggerProvider.Prune(logDirectory, logRetentionDays, DateTime.Today);
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
