using System.Globalization;

namespace Gnb.Clocking.Infrastructure.Photos;

/// <summary>
/// Keeps the kiosk's local photo copies from filling a small disk. The clock server holds the canonical
/// photo; the local copy only feeds the on-screen activity list, so days older than the retention window
/// (<c>{root}/Candidates/{id}/Records/{yyyy-MM-dd}</c>) are deleted.
/// </summary>
public static class LocalPhotoPruner
{
    public const int DefaultKeepDays = 14;

    /// <returns>The number of day folders removed.</returns>
    public static int Prune(string root, int keepDays, DateTime today)
    {
        var candidates = Path.Combine(root, "Candidates");
        if (keepDays < 1 || !Directory.Exists(candidates))
            return 0;

        var cutoff = today.Date.AddDays(-keepDays);
        var removed = 0;
        foreach (var candidate in Directory.EnumerateDirectories(candidates))
        {
            var records = Path.Combine(candidate, "Records");
            if (!Directory.Exists(records))
                continue;

            foreach (var day in Directory.EnumerateDirectories(records))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(day), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) || date >= cutoff)
                    continue;

                try
                {
                    Directory.Delete(day, recursive: true);
                    removed++;
                }
                catch (IOException)
                {
                    // In use (e.g. shown on screen right now); the next run gets it.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            TryDeleteIfEmpty(records);
            TryDeleteIfEmpty(candidate);
        }

        return removed;
    }

    private static void TryDeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
