using System.Diagnostics;
using WinPilot.Core.Programs;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class WindowsProgramCatalogTest
{
    private static WindowsProgramCatalog.Entry Entry(
        int id,
        int parent = 0,
        bool window = false,
        string name = "app.exe",
        string? package = null,
        long started = 100,
        int session = 1,
        string? path = null) =>
        new(new RunningProgram(id, name, "User", session) { WindowTitles = window ? ["窗口标题"] : [] },
            parent, started, name == "node.exe" ? "Node.js" : name, package, package is null ? null : "包应用",
            path ?? Path.Combine(@"C:\Apps", name));

    [Fact]
    public void WindowApplicationCollectsWindowlessDescendantsButNotIndependentWindows()
    {
        var rows = WindowsProgramCatalog.Group([
            Entry(1, window: true),
            Entry(2, 1, name: "node.exe"),
            Entry(3, 2),
            Entry(4, 1, window: true),
            Entry(5, 4)
]);
        Assert.Equal(rows[0].GroupId, rows[1].GroupId);
        Assert.Equal(rows[0].GroupId, rows[2].GroupId);
        Assert.NotEqual(rows[0].GroupId, rows[3].GroupId);
        Assert.Equal(rows[3].GroupId, rows[4].GroupId);
        Assert.Equal("Node.js", rows[1].DisplayName);
        Assert.Equal("窗口标题", Assert.Single(rows[0].WindowTitles));
        Assert.Empty(rows[1].WindowTitles);
        Assert.Equal(5, rows.Select(row => row.ProcessId).Distinct().Count());
    }

    [Fact]
    public void ExplorerDoesNotCollectLaunchedApplicationsAndHiddenInstancesMergeByPath()
    {
        var rows = WindowsProgramCatalog.Group([Entry(1, window: true, name: "explorer.exe"),
            Entry(2, 1), Entry(3, 2), Entry(4)]);
        Assert.Equal(2, rows.Select(row => row.GroupId).Distinct().Count());
        Assert.NotEqual(rows[0].GroupId, rows[1].GroupId);
        Assert.Equal(rows[1].GroupId, rows[3].GroupId);
    }

    [Fact]
    public void HiddenInstancesUseFullPathUserAndSessionRatherThanNameOrWindowHistory()
    {
        var otherUser = Entry(4, path: @"C:\QQ\QQ.exe") with
        {
            Program = new RunningProgram(4, "QQ.exe", "OtherUser", 1)
        };
        var rows = WindowsProgramCatalog.Group([
            Entry(1, name: "QQ.exe", path: @"C:\QQ\QQ.exe"),
            Entry(2, name: "QQ.exe", path: @"c:\qq\qq.EXE"),
            Entry(3, path: @"C:\Other\QQ.exe"),
            otherUser,
            Entry(5, session: 2, path: @"C:\QQ\QQ.exe"),
            Entry(6, path: ""),
            Entry(7, path: "")
]);
        Assert.Equal(rows[0].GroupId, rows[1].GroupId);
        Assert.Equal(6, rows.Select(row => row.GroupId).Distinct().Count());
        Assert.All(rows, row => Assert.Empty(row.WindowTitles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrayApplicationKeepsItsOwnHelpersWithoutAbsorbingLaunchersOrIndependentApplications(bool window)
    {
        var rows = WindowsProgramCatalog.Group([
            Entry(1, window: true, name: "Nexus.exe", path: @"C:\Nexus\Nexus.exe"),
            Entry(2, 1, window: window, name: "QQ.exe", path: @"C:\QQ\QQ.exe"),
            Entry(3, 2, name: "QQ.exe", path: @"C:\QQ\QQ.exe"),
            Entry(4, 3, name: "crashpad_handler.exe", path: @"C:\QQ\crashpad_handler.exe"),
            Entry(5, 2, name: "conhost.exe", path: @"C:\Windows\System32\conhost.exe"),
            Entry(6, 2, window: true, path: @"C:\QQ\independent.exe"),
            Entry(7, 2, package: "independent-package", path: @"C:\QQ\packaged.exe"),
            Entry(
                8,
                2,
                name: "QQScreenshot.exe",
                path: @"C:\QQ\versions\9.9\resources\app\QQScreenShot\BinRelease-x64\QQScreenshot.exe"),
            Entry(9, 2, name: "other.exe", path: @"C:\QQ2\other.exe"),
            Entry(10, 2, name: "outside.exe", path: @"C:\QQ\..\Other\outside.exe")
]);
        Assert.Equal(rows[1].GroupId, rows[2].GroupId);
        Assert.Equal(rows[1].GroupId, rows[3].GroupId);
        Assert.Equal(rows[1].GroupId, rows[7].GroupId);
        foreach (var index in new[] {
            0,
            5,
            6
})
        {
            Assert.NotEqual(rows[1].GroupId, rows[index].GroupId);
        }
        foreach (var index in new[] {
            4,
            8,
            9
})
        {
            Assert.Equal(window, rows[1].GroupId == rows[index].GroupId);
        }
        Assert.Equal("QQ.exe", rows[1].GroupName);
        Assert.Equal("QQ.exe", rows[3].GroupProcessName);
        Assert.Equal("crashpad_handler.exe", rows[3].Name);
        Assert.Equal(10, rows.Select(row => row.ProcessId).Distinct().Count());
    }

    [Fact]
    public void PackagesMergeTheirProcessesAndDescendantsWithinTheirSession()
    {
        var rows = WindowsProgramCatalog.Group([
            Entry(1, package: "pkg"),
            Entry(2, window: true, package: "pkg"),
            Entry(3, 1, name: "node.exe"),
            Entry(4, package: "other"),
            Entry(5, package: "pkg", session: 2),
            Entry(6, 1, window: true)
]);
        Assert.Equal(rows[0].GroupId, rows[1].GroupId);
        Assert.Equal(rows[0].GroupId, rows[2].GroupId);
        Assert.Equal("包应用", rows[0].GroupName);
        Assert.NotEqual(rows[0].GroupId, rows[3].GroupId);
        Assert.NotEqual(rows[0].GroupId, rows[4].GroupId);
        Assert.Equal(rows[0].GroupId, rows[5].GroupId);
    }

    [Fact]
    public void WindowTitlesPreserveSeparateWindowsAndResolveUwpOwnership()
    {
        var windows = WindowsProgramCatalog.AssociateWindows([(1, "同名窗口"), (1, "同名窗口")],
            [(2, "设置"), (0, "托管窗口"), (0, "无匹配窗口")], [(3, "托管窗口")]);
        Assert.Equal(2, windows[1].Length);
        Assert.Equal("设置", Assert.Single(windows[2]));
        Assert.Equal("托管窗口", Assert.Single(windows[3]));
        Assert.Equal(3, windows.Count);
    }

    [Fact]
    public void InvalidParentRelationsDoNotAbsorbUnrelatedProcesses()
    {
        var rows = WindowsProgramCatalog.Group([
            Entry(1, window: true, started: 200),
            Entry(2, 1),
            Entry(3, 1, session: 2),
            Entry(4, 5),
            Entry(5, 4),
            Entry(6)
]);
        Assert.NotEqual(rows[0].GroupId, rows[1].GroupId);
        Assert.NotEqual(rows[0].GroupId, rows[2].GroupId);
        Assert.Equal(6, rows.Count);
    }

    [Fact]
    public void NativeMetadataQueryPreservesInputAndFallsBackForExitedProcesses()
    {
        using var current = Process.GetCurrentProcess();
        RunningProgram[] input = [
            new(current.Id, current.ProcessName + ".exe", "User", current.SessionId),
            new(int.MaxValue, "exited.exe", "User", current.SessionId)
];
        var rows = WindowsProgramCatalog.Describe(input);
        Assert.Equal(input.Select(row => row.ProcessId), rows.Select(row => row.ProcessId));
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.DisplayName)));
        Assert.Equal("exited.exe", rows[1].DisplayName);
    }
}
