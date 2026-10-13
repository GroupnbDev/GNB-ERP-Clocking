using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Gnb.Clocking.Infrastructure.Diagnostics;

/// <summary>
/// One background writer for <c>{directory}/kiosk-yyyyMMdd.log</c>. Callers only enqueue a line, so a log
/// from the UI thread never waits on the disk. The queue is bounded (oldest lines drop on a stalled disk)
/// and the day file stays open between lines. <see cref="LogLevel.Critical"/> lines skip the queue.
/// Up to <see cref="CriticalPreambleCap"/> lines already waiting are written first, then the critical
/// line is flushed before <c>Log</c> returns. Crash handlers call this, and the process can end
/// immediately after. The cap keeps that handler from draining a full backlog on the way down.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int DefaultQueueCapacity = 10_000;
    public const int CriticalPreambleCap = 50;

    private readonly string _directory;
    private readonly Channel<string> _channel;
    private readonly object _fileGate = new();
    private readonly Task _writer;
    private StreamWriter? _file;
    private string? _filePath;
    private int _disposed;

    public FileLoggerProvider(string directory, int queueCapacity = DefaultQueueCapacity)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            // WriteNow also reads, and it can overlap the writer's WaitToReadAsync.
            SingleReader = false,
            SingleWriter = false
        });
        _writer = Task.Run(WriteLoop);
    }

    public string DirectoryPath => _directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

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

        lock (_fileGate)
        {
            _file?.Dispose();
            _file = null;
            _filePath = null;
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
                || !DateTime.TryParseExact(name["kiosk-".Length..], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)
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

    private void Enqueue(string line) => _channel.Writer.TryWrite(line);

    private void WriteNow(string line)
    {
        lock (_fileGate)
        {
            try
            {
                DrainQueued(CriticalPreambleCap);
                _file?.Flush();
            }
            catch (Exception)
            {
                // A failed preamble must not swallow the crash line. Reopen on the write below.
                _file?.Dispose();
                _file = null;
                _filePath = null;
            }

            try
            {
                Append(line);
                _file?.Flush();
            }
            catch (Exception)
            {
                // The kiosk keeps running if the log disk is full or locked.
            }
        }
    }

    /// <summary>Caller holds <see cref="_fileGate"/>.</summary>
    private void DrainQueued(int maxLines)
    {
        var reader = _channel.Reader;
        var drained = 0;
        while (drained < maxLines && reader.TryRead(out var queued))
        {
            Append(queued);
            drained++;
        }
    }

    private async Task WriteLoop()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            lock (_fileGate)
            {
                try
                {
                    while (reader.TryRead(out var line))
                        Append(line);
                    _file?.Flush();
                }
                catch (Exception)
                {
                    // The kiosk keeps running if the log disk is full or locked. Reopen on the next batch.
                    _file?.Dispose();
                    _file = null;
                    _filePath = null;
                }
            }
        }
    }

    /// <summary>Caller holds <see cref="_fileGate"/>. Switches to a new file when the day changes.</summary>
    private void Append(string line)
    {
        var path = Path.Combine(_directory, $"kiosk-{DateTime.Now:yyyyMMdd}.log");
        if (_file == null || !string.Equals(_filePath, path, StringComparison.Ordinal))
        {
            _file?.Dispose();
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _file = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _filePath = path;
        }

        _file.Write(line);
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _provider;

        public FileLogger(string category, FileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
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

            if (logLevel >= LogLevel.Critical)
                _provider.WriteNow(line);
            else
                _provider.Enqueue(line);
        }
    }
}
