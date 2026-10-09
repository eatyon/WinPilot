using WinPilot.Core;
using WinPilot.Features.Logs;
using WinPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace WinPilot.Test;

public sealed class RollingFileLoggerProviderTest
{
    [Fact]
    public void Log_CreatesDirectoryOnlyWhenWritingAndUsesDiagnosticFormat()
    {
        var logsDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"), "logs");

        try
        {
            using var provider = new RollingFileLoggerProvider(logsDirectory);
            Assert.False(Directory.Exists(logsDirectory));

            provider.CreateLogger("WinPilot.Test.Logging").LogInformation("日志写入验证");

            var logFile = Assert.Single(Directory.EnumerateFiles(logsDirectory, "winpilot*.log"));
            Assert.Matches($"^winpilot{DateTime.Now:yyyyMMdd}\\.log$", Path.GetFileName(logFile));
            var content = File.ReadAllText(logFile);
            Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[INF\] WinPilot\.Test\.Logging\r?\n日志写入验证\r?\n", content);
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
    public void Log_EscapesMultiLineMessageAsOneEntry()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            using var provider = new RollingFileLoggerProvider(paths.LogsDirectory);
            provider.CreateLogger("WinPilot.Test.Logging").LogInformation("第一行\n第二行\n\n第三行");

            var page = new LogQueryService(paths).Query(new LogQuery(DateOnly.FromDateTime(DateTime.Now), null, null, 0, 10));
            var entry = Assert.Single(page.Entries);
            Assert.Equal(@"第一行\n第二行\n\n第三行", entry.Message);
            Assert.Null(entry.Details);
        }
        finally
        {
            if (Directory.Exists(applicationDirectory)) Directory.Delete(applicationDirectory, recursive: true);
        }
    }
}
