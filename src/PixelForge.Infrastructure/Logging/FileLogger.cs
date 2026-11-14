using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PixelForge.Domain.Interfaces;

namespace PixelForge.Infrastructure.Logging;

public sealed class FileLogger : IAppLogger, ILoggerProvider, IDisposable
{
    private readonly Channel<LogEntry> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writerTask;
    private readonly object _stateLock = new();
    private volatile LogLevel _minimumLevel = LogLevel.Information;
    private string _logDirectory;
    private bool _disposed;

    public FileLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(20_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public string LogDirectory => _logDirectory;

    public void Initialize(string logDirectory, string minimumLevel)
    {
        lock (_stateLock)
        {
            _logDirectory = logDirectory;
            Directory.CreateDirectory(_logDirectory);
        }

        SetMinimumLevel(minimumLevel);
        CleanupOldLogs();
    }

    public void SetMinimumLevel(string level)
    {
        _minimumLevel = Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;
    }

    public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);

    public void LogInformation(string message, params object?[] args) =>
        Write(LogLevel.Information, "PixelForge", message, args, null);

    public void LogWarning(string message, params object?[] args) =>
        Write(LogLevel.Warning, "PixelForge", message, args, null);

    public void LogError(Exception? exception, string message, params object?[] args) =>
        Write(LogLevel.Error, "PixelForge", message, args, exception);

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel;

    internal void Write(LogLevel level, string category, string message, object?[] args, Exception? exception)
    {
        if (!IsEnabled(level)) return;
        var text = args.Length > 0 ? SafeFormat(message, args) : message;
        var entry = new LogEntry(DateTimeOffset.Now, level, category, text, exception);
        _channel.Writer.TryWrite(entry);
    }

    private static string SafeFormat(string message, object?[] args)
    {
        try
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, message, args);
        }
        catch
        {
            return message;
        }
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        DateOnly? currentDate = null;
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    var date = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);
                    if (writer is null || currentDate != date)
                    {
                        writer?.Dispose();
                        Directory.CreateDirectory(_logDirectory);
                        var path = Path.Combine(_logDirectory, $"pixelforge-{date:yyyyMMdd}.log");
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                            System.Text.Encoding.UTF8)
                        {
                            AutoFlush = true
                        };
                        currentDate = date;
                    }

                    var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelText(entry.Level)}] {entry.Category}: {entry.Message}";
                    await writer.WriteLineAsync(line);
                    if (entry.Exception is not null)
                    {
                        await writer.WriteLineAsync(entry.Exception.ToString());
                    }
                }
                catch
                {
                    // File logging must never crash the application.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            try
            {
                writer?.Dispose();
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };

    private void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "pixelforge-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    public void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Writer.TryComplete();
        try
        {
            _shutdown.CancelAfter(TimeSpan.FromSeconds(2));
            _writerTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Ignore.
        }
    }

    public void Dispose()
    {
        Shutdown();
        _shutdown.Dispose();
    }

    private sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, Exception? Exception);

    private sealed class CategoryLogger : ILogger
    {
        private readonly FileLogger _owner;
        private readonly string _category;

        public CategoryLogger(FileLogger owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _owner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _owner.Write(logLevel, _category, formatter(state, exception), [], exception);
        }
    }
}
