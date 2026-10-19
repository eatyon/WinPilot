using WinPilot.Core.Programs;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class WindowsProgramManagerTest
{
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
