using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.Core.Logging;

/// <summary>Writes logs to daily files (iPhoneMiror-yyyyMMdd.log) on a background task and keeps an in-memory tail.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retainDays;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly ConcurrentQueue<string> _recent = new();

    public FileLoggerProvider(string directory, int retainDays = 7)
    {
        _directory = directory;
        _retainDays = Math.Max(1, retainDays);
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
        PruneOldFiles();
    }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    public string LogDirectory => _directory;

    /// <summary>Most recent log lines (up to 500), newest last.</summary>
    public IReadOnlyCollection<string> RecentLines => _recent.ToArray();

    public event EventHandler<string>? LineWritten;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line)
    {
        _recent.Enqueue(line);
        while (_recent.Count > 500 && _recent.TryDequeue(out _)) { }
        _channel.Writer.TryWrite(line);
        LineWritten?.Invoke(this, line);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var line in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var file = Path.Combine(_directory, $"iPhoneMiror-{DateTime.Now:yyyyMMdd}.log");
                await File.AppendAllTextAsync(file, line + Environment.NewLine, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Logging must never crash the app.
            }
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);
            foreach (var f in Directory.EnumerateFiles(_directory, "iPhoneMiror-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder();
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            sb.Append(" [").Append(Short(logLevel)).Append("] ");
            var dot = category.LastIndexOf('.');
            sb.Append(dot >= 0 ? category[(dot + 1)..] : category).Append(": ");
            sb.Append(formatter(state, exception));
            if (exception is not null) sb.Append(Environment.NewLine).Append(exception);
            owner.Enqueue(sb.ToString());
        }

        private static string Short(LogLevel l) => l switch
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
