using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core.Programs;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class WindowsProgramManagerTest
{
    [Fact]
    public void OwnProcessIsListedButCannotBeEnded()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var manager = new WindowsProgramManager(NullLogger<WindowsProgramManager>.Instance);
        var ownProcess = Assert.Single(manager.GetRunningPrograms(identity.Name), process => process.ProcessId == Environment.ProcessId);
        Assert.False(ownProcess.CanEnd);
        Assert.Contains(
            "WinPilot 自身进程",
            Assert.Throws<InvalidOperationException>(
            () => manager.Close([ownProcess.ProcessId], false)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseEndsOnlySelectedHelpersAndSkipsExitedMembers(bool isGroup)
    {
        using var selected = StartSleepingHelper();
        using var unselected = StartSleepingHelper();
        try
        {
            var manager = new WindowsProgramManager(NullLogger<WindowsProgramManager>.Instance);
            manager.Close([int.MaxValue, selected.Id, selected.Id], isGroup);
            Assert.True(selected.WaitForExit(5000));
            Assert.False(unselected.HasExited);
        }
        finally
        {
            foreach (var helper in new[] { selected, unselected })
            {
                if (!helper.HasExited)
                {
                    helper.Kill();
                    helper.WaitForExit(5000);
                }
            }
        }
    }

    [Fact]
    public void CloseValidatesEveryMemberBeforeEndingAnyProcess()
    {
        using var helper = StartSleepingHelper();
        try
        {
            var manager = new WindowsProgramManager(NullLogger<WindowsProgramManager>.Instance);
            Assert.Contains(
                "WinPilot 自身进程",
                Assert.Throws<InvalidOperationException>(
                () => manager.Close([helper.Id, Environment.ProcessId], false)).Message);
            Assert.False(helper.HasExited);
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill();
                helper.WaitForExit(5000);
            }
        }
    }

    private static Process StartSleepingHelper() =>
        Process.Start(new ProcessStartInfo(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            "-NoProfile -NonInteractive -Command \"[Threading.Thread]::Sleep(30000)\"")
        { UseShellExecute = false, CreateNoWindow = true })!;

    [Fact]
    public void UsageIgnoresMembersThatExitBetweenSamplesWithoutSubtractingTheirCounters()
    {
        var now = DateTimeOffset.UnixEpoch;
        var first = new WindowsProgramManager.Snapshot(now, new Dictionary<int, WindowsProgramManager.ProcessSnapshot>
        {
            [1] = new("exited.exe", TimeSpan.FromSeconds(100), 4096, 100000),
            [2] = new("alive.exe", TimeSpan.FromSeconds(10), 1024, 1000)
        });
        var second = new WindowsProgramManager.Snapshot(now.AddSeconds(1), new Dictionary<int, WindowsProgramManager.ProcessSnapshot>
        {
            [2] = new("alive.exe", TimeSpan.FromSeconds(11), 2048, 3000)
        });
        var usage = WindowsProgramManager.CalculateUsage(first, second, 4);
        Assert.Equal([2], usage.ProcessIds);
        Assert.Equal("alive.exe", usage.Name);
        Assert.Equal(25, usage.CpuPercent);
        Assert.Equal(2048, usage.MemoryBytes);
        Assert.Equal(2000, usage.DiskBytesPerSecond);
    }

    [Fact]
    public void UsageReportsWhenEveryMemberExitsDuringSampling()
    {
        var now = DateTimeOffset.UnixEpoch;
        var first = new WindowsProgramManager.Snapshot(now, new Dictionary<int, WindowsProgramManager.ProcessSnapshot>
        {
            [1] = new("exited.exe", TimeSpan.FromSeconds(1), 1024, 1000)
        });
        var second = new WindowsProgramManager.Snapshot(now.AddSeconds(1), new Dictionary<int, WindowsProgramManager.ProcessSnapshot>());
        Assert.Equal(
            "进程已退出",
            Assert.Throws<InvalidOperationException>(() => WindowsProgramManager.CalculateUsage(first, second, 4)).Message);
    }

    [Fact]
    public void ExitedMembersAreSkippedWhileEmptySelectionStillFails()
    {
        var manager = new WindowsProgramManager(NullLogger<WindowsProgramManager>.Instance);
        Assert.Equal(
            "进程已退出",
            Assert.Throws<InvalidOperationException>(() => manager.GetUsage([int.MaxValue], CancellationToken.None)).Message);
        manager.Close([int.MaxValue], true);
        manager.Close([int.MaxValue], false);
        Assert.Equal("请选择至少一个进程", Assert.Throws<InvalidOperationException>(() => manager.Close([], false)).Message);
        Assert.Equal(
            "请选择至少一个进程",
            Assert.Throws<InvalidOperationException>(() => manager.GetUsage([], CancellationToken.None)).Message);
    }

    [Theory]
    [InlineData(".exe", false)]
    [InlineData(".exe", true)]
    [InlineData(".bat", false)]
    [InlineData(".cmd", true)]
    [InlineData(".vbs", false)]
    [InlineData(".py", true)]
    public void EmptyWorkingDirectoryUsesTargetDirectoryForProgramsAndScripts(string extension, bool elevated)
    {
        var file = Path.Combine(Path.GetTempPath(), "WinPilot test folder", "app" + extension);
        var original = new ProgramLaunchRequest(file, "--example", "", elevated, true);
        var prepared = WindowsProgramManager.PrepareLaunchRequest(original);
        Assert.Equal(Path.GetDirectoryName(file), prepared.WorkingDirectory);
        Assert.Equal(file, prepared.ProgramPath);
        Assert.Equal(original.Arguments, prepared.Arguments);
        Assert.Equal(original.RunElevated, prepared.RunElevated);
        Assert.Equal(original.Silent, prepared.Silent);
        Assert.Equal("", original.WorkingDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelativeTargetIsResolvedBeforeDerivingWorkingDirectory(bool elevated)
    {
        var relative = Path.Combine("tools", "scripts", "app.py");
        var prepared = WindowsProgramManager.PrepareLaunchRequest(new(relative, "", "", elevated));
        Assert.Equal(Path.GetFullPath(relative), prepared.ProgramPath);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(relative)), prepared.WorkingDirectory);
    }

    [Theory]
    [InlineData("C:\\custom folder", false)]
    [InlineData("C:\\custom folder", true)]
    [InlineData("relative-folder", false)]
    [InlineData("relative-folder", true)]
    public void ExplicitWorkingDirectoryIsPreserved(string directory, bool elevated)
    {
        var prepared = WindowsProgramManager.PrepareLaunchRequest(new("app.exe", "", directory, elevated));
        Assert.Equal(directory, prepared.WorkingDirectory);
    }

    [Fact]
    public void WhitespaceWorkingDirectoryIsTreatedAsEmpty()
    {
        var prepared = WindowsProgramManager.PrepareLaunchRequest(new("app.exe", "", "  ", false));
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath("app.exe")), prepared.WorkingDirectory);
    }
}
