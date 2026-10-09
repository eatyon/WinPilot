using WinPilot.Core;
using WinPilot.Features.Logs;

namespace WinPilot.Test;

public sealed class LogQueryServiceTest
{
    [Fact]
    public void SnapshotKeepsPaginationStableWhenIdenticalEntriesAreAppended()
    {
        WithLogs((paths, service) =>
        {
            var date = new DateOnly(2026, 10, 4);
            var file = Path.Combine(paths.LogsDirectory, "winpilot20261004.log");
            var text = "[10:00:00.000] [INF] Test\nsame\n\n";
            File.WriteAllText(file, string.Concat(Enumerable.Repeat(text, 3)));
            var first = service.Query(new(date, null, null, 0, 1));
            Assert.Equal(3, first.Snapshot);
            Assert.Equal(3, Assert.Single(first.Entries).Id);
            File.AppendAllText(file, text);
            var second = service.Query(new(date, null, null, 1, 1, first.Snapshot));
            var third = service.Query(new(date, null, null, 2, 1, first.Snapshot));
            Assert.Equal(2, Assert.Single(second.Entries).Id);
            Assert.Equal(1, Assert.Single(third.Entries).Id);
            Assert.False(third.HasMore);
            Assert.Equal(4, Assert.Single(service.Query(new(date, null, null, 0, 1)).Entries).Id);
        });
    }

    [Fact]
    public void IncompleteEntryIsExcludedUntilItsTerminatingBlankLineArrives()
    {
        WithLogs((paths, service) =>
        {
            var date = new DateOnly(2026, 10, 4);
            var file = Path.Combine(paths.LogsDirectory, "winpilot20261004.log");
            File.WriteAllText(file, "[10:00:00.000] [INF] Test\ncomplete\n\n[10:00:01.000] [ERR] Test\npartial");
            Assert.Equal("complete", Assert.Single(service.Query(new(date, null, null, 0, 500)).Entries).Message);
            File.AppendAllText(file, "\n\n");
            Assert.Equal(2, service.Query(new(date, null, null, 0, 500)).Entries.Count);
        });
    }

    [Fact]
    public void PageSizeAndLoadMoreStopAtTheSupportedOffset()
    {
        WithLogs((paths, service) =>
        {
            var date = new DateOnly(2026, 10, 4);
            File.WriteAllText(Path.Combine(paths.LogsDirectory, "winpilot20261004.log"),
                string.Concat(Enumerable.Repeat("[10:00:00.000] [INF] Test\nentry\n\n", 11000)));
            Assert.Equal(500, service.Query(new(date, null, null, 0, 1000)).Entries.Count);
            var last = service.Query(new(date, null, null, 10000, 500));
            Assert.Equal(500, last.Entries.Count);
            Assert.False(last.HasMore);
            Assert.Throws<ArgumentOutOfRangeException>(() => service.Query(new(date, null, null, 10001, 500)));
        });
    }

    [Fact]
    public void MissingFilesCancellationAndInvalidSnapshotReleaseResources()
    {
        WithLogs((paths, service) =>
        {
            var date = new DateOnly(2026, 10, 4);
            Assert.Empty(service.Query(new(date, null, null, 0, 500)).Entries);
            Assert.Throws<ArgumentOutOfRangeException>(() => service.Query(new(date, null, null, 0, 500, -1)));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => service.Query(new(date, null, null, 0, 500), cancellation.Token));
            var file = Path.Combine(paths.LogsDirectory, "winpilot20261004.log");
            File.WriteAllText(file, "[10:00:00.000] [INF] Test\nentry\n\n");
            service.Query(new(date, null, null, 0, 500));
            using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() => service.Query(new(date, null, null, 0, 500)));
        });
    }

    [Fact]
    public void AvailableDatesExcludeInvalidFilesAndRemainNewestFirst()
    {
        WithLogs((paths, service) =>
        {
            foreach (var name in new[] { "winpilot20261004.log", "winpilot20260930.log", "winpilot20260230.log", "winpilotarchive.log" })
                File.WriteAllText(Path.Combine(paths.LogsDirectory, name), "");
            Assert.Equal(new[] { new DateOnly(2026, 10, 4), new DateOnly(2026, 9, 30) }, service.GetAvailableDates().Select(date => date.Value));
        });
    }

    private static void WithLogs(Action<PortableDataPaths, LogQueryService> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.LogQuery.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            Directory.CreateDirectory(paths.LogsDirectory);
            action(paths, new LogQueryService(paths));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Query_ReturnsLatestMatchingBatchAndPreservesExceptionDetails()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            Directory.CreateDirectory(paths.LogsDirectory);
            var date = new DateOnly(2026, 8, 30);
            File.WriteAllText(Path.Combine(paths.LogsDirectory, "winpilot20260830.log"), "[10:00:00.001] [INF] WinPilot.Test.Source\n第一条日志\n\n[10:00:01.002] [ERR] WinPilot.Test.Source\n发生错误\nSystem.InvalidOperationException: 测试异常\n   at Test.Method()\n\n[10:00:02.003] [DBG] WinPilot.Other\n最后一条日志\n\n");

            var service = new LogQueryService(paths);
            var page = service.Query(new LogQuery(date, null, null, 0, 2));
            var errors = service.Query(new LogQuery(date, new HashSet<string>(["ERR"], StringComparer.OrdinalIgnoreCase), "Source", 0, 500));
            var selectedLevels = new HashSet<string>(["INF", "ERR"], StringComparer.OrdinalIgnoreCase);
            var mixedFirst = service.Query(new LogQuery(date, selectedLevels, null, 0, 1));
            var mixedSecond = service.Query(new LogQuery(date, selectedLevels, null, 1, 1));

            Assert.Equal(["最后一条日志", "发生错误"], page.Entries.Select(entry => entry.Message));
            Assert.True(page.HasMore);
            Assert.Single(errors.Entries);
            Assert.Contains("System.InvalidOperationException", errors.Entries[0].Details);
            Assert.Equal("发生错误", Assert.Single(mixedFirst.Entries).Message);
            Assert.True(mixedFirst.HasMore);
            Assert.Equal("第一条日志", Assert.Single(mixedSecond.Entries).Message);
            Assert.False(mixedSecond.HasMore);
            Assert.Equal(date, Assert.Single(service.GetAvailableDates()).Value);
        }
        finally
        {
            if (Directory.Exists(applicationDirectory))
            {
                Directory.Delete(applicationDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void GetAvailableDates_DoesNotCreateLogsDirectory()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var service = new LogQueryService(paths);

            Assert.Empty(service.GetAvailableDates());
            Assert.False(Directory.Exists(paths.LogsDirectory));
        }
        finally
        {
            if (Directory.Exists(applicationDirectory))
            {
                Directory.Delete(applicationDirectory, recursive: true);
            }
        }
    }
}
