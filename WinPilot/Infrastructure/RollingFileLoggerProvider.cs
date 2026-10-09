using System.Diagnostics;

namespace WinPilot.Infrastructure;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 21;
    private const int RetainedFileCountLimit = 31;
    private readonly object syncRoot = new();
    private readonly string logsDirectory;
    private bool disposed;
    private long lastFallbackWriteTicks;
    private DateOnly? lastCleanupDate;

    public RollingFileLoggerProvider(string logsDirectory)
    {
        this.logsDirectory = logsDirectory;
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    public void Dispose()
    {
        lock (syncRoot) disposed = true;
    }

    private void Write(string categoryName, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            lock (syncRoot)
            {
                if (disposed) return;
                Directory.CreateDirectory(logsDirectory);
                var now = DateTimeOffset.Now;
                var logFile = Path.Combine(logsDirectory, $"winpilot{now:yyyyMMdd}.log");
                WriteEntry(logFile, CreateEntry(now, categoryName, level, message, exception));

                var today = DateOnly.FromDateTime(now.DateTime);
                if (lastCleanupDate != today)
                {
                    var deletedCount = DeleteExpiredFiles();
                    lastCleanupDate = today;
                    if (deletedCount > 0)
                    {
                        WriteEntry(logFile, CreateEntry(
                            DateTimeOffset.Now,
                            typeof(RollingFileLoggerProvider).FullName!,
                            LogLevel.Information,
                            $"日志维护已清理 {deletedCount} 个过期或超出保留数量的日志文件",
                            null));
                    }
                }
            }
        }
        catch (Exception writeException)
        {
            WriteFallback(writeException);
        }
    }

    private void WriteFallback(Exception exception)
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref lastFallbackWriteTicks);
        if (now - previous < TimeSpan.FromMinutes(1).Ticks || Interlocked.CompareExchange(ref lastFallbackWriteTicks, now, previous) != previous)
        {
            return;
        }

        var message = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] WinPilot 文件日志写入失败：{exception}";
        Debug.WriteLine(message);
        try { Console.Error.WriteLine(message); } catch { }
    }

    private int DeleteExpiredFiles()
    {
        if (!Directory.Exists(logsDirectory))
        {
            return 0;
        }

        var expiry = DateTime.UtcNow.AddDays(-RetentionDays);
        var deletedCount = 0;
        foreach (var file in Directory.EnumerateFiles(logsDirectory, "winpilot*.log*"))
        {
            if (File.GetLastWriteTimeUtc(file) < expiry)
            {
                File.Delete(file);
                deletedCount++;
            }
        }

        var retainedFiles = Directory.EnumerateFiles(logsDirectory, "winpilot*.log*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in retainedFiles.Skip(RetainedFileCountLimit))
        {
            File.Delete(file);
            deletedCount++;
        }

        return deletedCount;
    }

    private static void WriteEntry(string logFile, string entry)
    {
        using var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.Write(entry);
    }

    private static string CreateEntry(DateTimeOffset timestamp, string categoryName, LogLevel level, string message, Exception? exception)
    {
        var header = $"[{timestamp:HH:mm:ss.fff}] [{GetLevelAbbreviation(level)}] {categoryName}";
        var escapedMessage = message.Replace("\r", "\\r").Replace("\n", "\\n");
        var details = exception is null ? string.Empty : exception + Environment.NewLine;
        return $"{header}{Environment.NewLine}{escapedMessage}{Environment.NewLine}{details}{Environment.NewLine}";
    }

    private static string GetLevelAbbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "FTL",
        _ => "UNK"
    };

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(categoryName, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }
}
