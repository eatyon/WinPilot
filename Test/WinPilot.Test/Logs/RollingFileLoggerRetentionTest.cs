using WinPilot.Core;
using WinPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace WinPilot.Test;

public sealed class RollingFileLoggerRetentionTest
{
    [Fact]
    public void Log_RemovesExpiredFilesAndRecordsMaintenance()
    {
        var logsDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"), "logs");
        Directory.CreateDirectory(logsDirectory);
        var expiredLog = Path.Combine(logsDirectory, "winpilot20000101.log");
        File.WriteAllText(expiredLog, "旧日志");
        File.SetLastWriteTimeUtc(expiredLog, DateTime.UtcNow.AddDays(-22));

        try
        {
            using var provider = new RollingFileLoggerProvider(logsDirectory);
            provider.CreateLogger("WinPilot.Test.Retention").LogInformation("触发日志维护");

            Assert.False(File.Exists(expiredLog));
            var currentLog = Assert.Single(Directory.EnumerateFiles(logsDirectory, "winpilot*.log"));
            Assert.Contains("日志维护已清理 1 个过期或超出保留数量的日志文件", File.ReadAllText(currentLog));
        }
        finally
        {
            var applicationDirectory = Directory.GetParent(logsDirectory)?.FullName;
            if (applicationDirectory is not null && Directory.Exists(applicationDirectory))
            {
                Directory.Delete(applicationDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_RetainsAtMostThirtyOneFiles()
    {
        var logsDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"), "logs");
        Directory.CreateDirectory(logsDirectory);
        var now = DateTime.UtcNow;

        try
        {
            for (var index = 0; index < 31; index++)
            {
                var logFile = Path.Combine(logsDirectory, $"winpilotarchive{index:D2}.log");
                File.WriteAllText(logFile, "历史日志");
                File.SetLastWriteTimeUtc(logFile, now.AddMinutes(-index - 1));
            }

            using var provider = new RollingFileLoggerProvider(logsDirectory);
            provider.CreateLogger("WinPilot.Test.Retention").LogInformation("触发数量清理");

            Assert.Equal(31, Directory.EnumerateFiles(logsDirectory, "winpilot*.log*").Count());
        }
        finally
        {
            var applicationDirectory = Directory.GetParent(logsDirectory)?.FullName;
            if (applicationDirectory is not null && Directory.Exists(applicationDirectory))
            {
                Directory.Delete(applicationDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Log_DoesNotRotateWhenCurrentDayFileIsLarge()
    {
        var logsDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"), "logs");
        Directory.CreateDirectory(logsDirectory);
        var currentLog = Path.Combine(logsDirectory, $"winpilot{DateTime.Now:yyyyMMdd}.log");

        try
        {
            File.WriteAllText(currentLog, new string('x', 11 * 1024 * 1024));

            using var provider = new RollingFileLoggerProvider(logsDirectory);
            provider.CreateLogger("WinPilot.Test.Retention").LogInformation("继续写入当日日志");

            Assert.Single(Directory.EnumerateFiles(logsDirectory, "winpilot*.log"));
            Assert.Empty(Directory.EnumerateFiles(logsDirectory).Where(file => Path.GetFileName(file).Contains(".log.", StringComparison.Ordinal)));
        }
        finally
        {
            var applicationDirectory = Directory.GetParent(logsDirectory)?.FullName;
            if (applicationDirectory is not null && Directory.Exists(applicationDirectory))
            {
                Directory.Delete(applicationDirectory, recursive: true);
            }
        }
    }
}
