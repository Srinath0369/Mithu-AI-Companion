using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mithu.Infrastructure.Logging;

/// <summary>
/// Minimal, dependency-free daily rolling file logger.
/// Writes asynchronously on a background thread so the UI thread is never blocked by disk I/O.
/// Privacy rule: callers must never log note or reminder content.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 1024);
    private readonly Thread _writerThread;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly TimeProvider _timeProvider;

    public FileLoggerProvider(string directory, TimeProvider timeProvider, int retentionDays = 7)
    {
        _directory = directory;
        _timeProvider = timeProvider;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_directory);
        DeleteOldLogs();

        _writerThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "Mithu.FileLogger",
            Priority = ThreadPriority.BelowNormal,
        };
        _writerThread.Start();
    }

    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void Enqueue(string line)
    {
        // Drop messages rather than block the caller if the queue is ever full.
        _queue.TryAdd(line);
    }

    internal DateTimeOffset Now => _timeProvider.GetLocalNow();

    private void ProcessQueue()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var file = Path.Combine(_directory, $"mithu-{Now:yyyyMMdd}.log");
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // Logging must never crash the app.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Collection completed.
        }
    }

    private void DeleteOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "mithu-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= provider.MinimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var line = $"{provider.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{ShortLevel(logLevel)}] {shortCategory}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Enqueue(line);
        }

        private static string ShortLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
