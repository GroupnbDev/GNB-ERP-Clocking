using System.Reflection;
using Gnb.Clocking.Infrastructure.ClockKiosk;
using Microsoft.Extensions.Configuration;

namespace Gnb.Clocking.App.Configuration;

/// <summary>
/// Kiosk settings, later sources win:
/// embedded <c>appsettings.json</c> → embedded <c>appsettings.Local.json</c> →
/// <c>{AppData}/clockkiosk.settings.json</c> → a readable <c>.env</c> beside the project,
/// otherwise the <c>.env</c> embedded at build → environment variables.
/// </summary>
public static class KioskConfiguration
{
    public const string DeviceSettingsFileName = "clockkiosk.settings.json";

    public static ClockKioskApiOptions Load(ConfigurationManager configuration, string appDataDirectory)
    {
        var assembly = typeof(KioskConfiguration).Assembly;
        AddEmbeddedJson(configuration, assembly, "appsettings.json");
        AddEmbeddedJson(configuration, assembly, "appsettings.Local.json");
        configuration.AddJsonFile(Path.Combine(appDataDirectory, DeviceSettingsFileName), optional: true, reloadOnChange: false);
        configuration.AddInMemoryCollection(ReadDotEnv());
        configuration.AddEnvironmentVariables();

        return new ClockKioskApiOptions
        {
            BaseUrl = configuration[$"{ClockKioskApiOptions.SectionName}:BaseUrl"],
            ApiKey = configuration[$"{ClockKioskApiOptions.SectionName}:ApiKey"],
            TenantIds = configuration[$"{ClockKioskApiOptions.SectionName}:TenantIds"],
            OrganizationIds = configuration[$"{ClockKioskApiOptions.SectionName}:OrganizationIds"],
            PhotoRoot = appDataDirectory,
        };
    }

    /// <summary>
    /// Reads a gitignored <c>.env</c>. A file the process can open wins over the copy
    /// embedded at build, so a port change applies without a rebuild when the file is readable.
    /// Only <c>ClockKiosk__*</c> keys are imported.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string?>> ReadDotEnv()
    {
        var lines = ReadExternalLines() ?? ReadEmbeddedLines();
        if (lines == null)
            return Array.Empty<KeyValuePair<string, string?>>();

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var split = line.IndexOf('=');
            if (split <= 0)
                continue;

            var key = line[..split].Trim();
            if (!key.StartsWith("ClockKiosk__", StringComparison.Ordinal))
                continue;

            var value = line[(split + 1)..].Trim().Trim('"');
            values[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        return values;
    }

    private static string[]? ReadEmbeddedLines()
    {
        var stream = typeof(KioskConfiguration).Assembly.GetManifestResourceStream(".env");
        if (stream == null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }

    private static string[]? ReadExternalLines()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && dir != null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            try
            {
                if (File.Exists(candidate))
                    return File.ReadAllLines(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private static void AddEmbeddedJson(ConfigurationManager configuration, Assembly assembly, string resourceName)
    {
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
            configuration.AddJsonStream(stream);
    }
}
