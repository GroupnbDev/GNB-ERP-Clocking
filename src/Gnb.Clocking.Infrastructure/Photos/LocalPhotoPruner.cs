using System.Globalization;
using System.Text.RegularExpressions;

namespace Gnb.Clocking.Infrastructure.Photos;

/// <summary>
/// Keeps the kiosk's local photo copies from filling a small disk. The clock server holds the canonical
/// photo; the local copy only feeds the on-screen activity list, so every file under <c>{root}/Users/{id}</c>
/// older than the retention window is deleted. The server picks the folder layout (a month folder with the
/// day in the file name), so a photo's age comes from, in order: a <c>yyyy-MM-dd</c> folder above it, a
/// <c>yyyy-MM-dd</c> or <c>yyyyMMdd</c> token in the file name, then the file's last-write time.
/// </summary>
public static partial class LocalPhotoPruner
{
    public const int DefaultKeepDays = 7;

    [GeneratedRegex(@"(?<![0-9])(\d{4})-?(\d{2})-?(\d{2})(?![0-9])")]
    private static partial Regex DayToken();

    /// <returns>The number of photo files removed.</returns>
    public static int Prune(string root, int keepDays, DateTime today)
    {
        var users = Path.Combine(root, "Users");
        if (keepDays < 1 || !Directory.Exists(users))
            return 0;

        var cutoff = today.Date.AddDays(-keepDays);
        var removed = 0;
        foreach (var user in Directory.EnumerateDirectories(users))
        {
            foreach (var file in Directory.EnumerateFiles(user, "*", SearchOption.AllDirectories).ToList())
            {
                if (PhotoDay(user, file) >= cutoff)
                    continue;

                try
                {
                    File.Delete(file);
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

            DeleteEmptyTree(user);
        }

        return removed;
    }

    private static DateTime PhotoDay(string userFolder, string file)
    {
        var folder = Path.GetDirectoryName(file);
        while (!string.IsNullOrEmpty(folder) && folder.Length > userFolder.Length)
        {
            if (DateTime.TryParseExact(Path.GetFileName(folder), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var folderDay))
                return folderDay;
            folder = Path.GetDirectoryName(folder);
        }

        var match = DayToken().Match(Path.GetFileName(file));
        if (match.Success && DateTime.TryParseExact(
                match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value, "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var tokenDay))
            return tokenDay;

        return File.GetLastWriteTime(file).Date;
    }

    private static void DeleteEmptyTree(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;

            foreach (var child in Directory.EnumerateDirectories(folder).ToList())
                DeleteEmptyTree(child);

            if (!Directory.EnumerateFileSystemEntries(folder).Any())
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
