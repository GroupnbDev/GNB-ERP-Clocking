using Gnb.Clocking.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Gnb.Clocking.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnb-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string TodayFile() => Path.Combine(_root, $"kiosk-{DateTime.Now:yyyyMMdd}.log");

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Critical_line_is_on_disk_as_soon_as_log_returns()
    {
        using var provider = new FileLoggerProvider(_root);
        var logger = provider.CreateLogger("Crash");

        logger.LogCritical(new InvalidOperationException("boom"), "Unhandled exception");

        var text = ReadShared(TodayFile());
        Assert.Contains("Critical Crash Unhandled exception", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void Critical_line_is_written_after_lines_already_queued()
    {
        using var provider = new FileLoggerProvider(_root);
        var logger = provider.CreateLogger("Crash");

        logger.LogInformation("context before");
        logger.LogCritical("Unhandled exception");

        var text = ReadShared(TodayFile());
        var contextAt = text.IndexOf("context before", StringComparison.Ordinal);
        var criticalAt = text.IndexOf("Critical Crash Unhandled exception", StringComparison.Ordinal);
        Assert.True(contextAt >= 0);
        Assert.True(criticalAt > contextAt);
    }

    [Fact]
    public void Dispose_flushes_every_queued_line_in_order()
    {
        var provider = new FileLoggerProvider(_root);
        var logger = provider.CreateLogger("Kiosk");
        for (var i = 0; i < 500; i++)
            logger.LogInformation("line {Index}", i);

        provider.Dispose();

        var lines = File.ReadAllLines(TodayFile());
        Assert.Equal(500, lines.Length);
        Assert.EndsWith("line 0", lines[0]);
        Assert.EndsWith("line 499", lines[^1]);
    }

    [Fact]
    public void Prune_keeps_recent_days_and_deletes_old_ones()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "kiosk-20260901.log"), "old");
        File.WriteAllText(Path.Combine(_root, "kiosk-20261010.log"), "recent");

        var removed = FileLoggerProvider.Prune(_root, 14, new DateTime(2026, 10, 12));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(Path.Combine(_root, "kiosk-20260901.log")));
        Assert.True(File.Exists(Path.Combine(_root, "kiosk-20261010.log")));
    }
}
