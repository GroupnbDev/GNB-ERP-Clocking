using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Gnb.Clocking.App.Diagnostics;

/// <summary>
/// One background writer for <c>{AppData}/logs/kiosk-yyyyMMdd.log</c>. Callers only enqueue a line,
/// so a log from the UI thread never waits on the disk.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Task _writer;
    private int _disposed;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoop);
    }

    public string DirectoryPath => _directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _channel.Writer);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _channel.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Shutting down. A lost line is better than blocking the process.
        }
    }

    /// <summary>Deletes <c>kiosk-yyyyMMdd.log</c> files whose date is older than <paramref name="retentionDays"/>.</summary>
    public static int Prune(string directory, int retentionDays, DateTime today)
    {
        if (retentionDays < 1 || !Directory.Exists(directory))
            return 0;

        var cutoff = today.Date.AddDays(-retentionDays);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "kiosk-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != "kiosk-yyyyMMdd".Length
                || !DateTime.TryParseExact(name["kiosk-".Length..], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date)
                || date >= cutoff)
                continue;

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private async Task WriteLoop()
    {
        await foreach (var line in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var path = Path.Combine(_directory, $"kiosk-{DateTime.Now:yyyyMMdd}.log");
                await File.AppendAllTextAsync(path, line).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The kiosk keeps running if the log disk is full or locked.
            }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly ChannelWriter<string> _writer;

        public FileLogger(string category, ChannelWriter<string> writer)
        {
            _category = category;
            _writer = writer;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel} {_category} {message}";
            if (exception != null)
                line += Environment.NewLine + exception;
            line += Environment.NewLine;
            _writer.TryWrite(line);
        }
    }
}
