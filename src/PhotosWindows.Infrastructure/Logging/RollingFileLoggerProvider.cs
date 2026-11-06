using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PhotosWindows.Infrastructure.Logging;

/// <summary>
/// Local-only log files (logs/photoswindows-yyyyMMdd.log), at most <see cref="MaxFiles"/> days and
/// <see cref="MaxBytesPerFile"/> per day. Nothing is ever sent over the network. Messages should not contain
/// personal paths; use file names only.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const int MaxFiles = 7;
    public const long MaxBytesPerFile = 10 * 1024 * 1024;
    private readonly string _folder;
    private readonly LogLevel _minimum;
    private readonly BlockingCollection<string> _queue = new(4096);
    private readonly Thread _writer;

    public RollingFileLoggerProvider(string folder, LogLevel minimum = LogLevel.Information)
    {
        _folder = folder;
        _minimum = minimum;
        Directory.CreateDirectory(folder);
        Prune();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "PhotosWindows log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            var path = Path.Combine(_folder, $"photoswindows-{DateTime.Now:yyyyMMdd}.log");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytesPerFile) continue;
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never crash the app; the line is dropped.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }
    }

    private void Prune()
    {
        foreach (var old in Directory.EnumerateFiles(_folder, "photoswindows-*.log").Order(StringComparer.Ordinal).Reverse().Skip(MaxFiles))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
                // In use; pruned next time.
            }
        }
    }

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
              .Append(logLevel.ToString().ToUpperInvariant()[..4]).Append(' ')
              .Append(category[(category.LastIndexOf('.') + 1)..]).Append(": ")
              .Append(formatter(state, exception));
            if (exception is not null) sb.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            sb.AppendLine();
            provider.Enqueue(sb.ToString());
        }
    }
}
