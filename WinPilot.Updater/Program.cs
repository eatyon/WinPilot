using System.Diagnostics;
using WinPilot.Updater;
using System.Runtime.InteropServices;

string? cleanupDirectory = null;
try
{
    var options = ParseArguments(args);
    var parentPid = int.Parse(GetRequired(options, "parent-pid"));
    var packagePath = Path.GetFullPath(GetRequired(options, "package-path"));
    var packageHash = GetRequired(options, "package-sha256");
    var targetDirectory = Path.GetFullPath(GetRequired(options, "target-dir"));
    var candidateCleanupDirectory = Path.GetFullPath(GetRequired(options, "cleanup-dir"));
    UpdatePackage.ValidateCleanupDirectory(candidateCleanupDirectory, targetDirectory);
    cleanupDirectory = candidateCleanupDirectory;
    var restartPath = Path.Combine(targetDirectory, "WinPilot.exe");
    var stagingDirectory = Path.Combine(cleanupDirectory, "staging");
    if (!packagePath.StartsWith(cleanupDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(packagePath)) throw new InvalidOperationException("更新包不存在");
    var packageRoot = await UpdatePackage.ExtractVerifiedAsync(packagePath, packageHash, stagingDirectory);

    try
    {
        using var parent = Process.GetProcessById(parentPid);
        if (!parent.WaitForExit(30000)) throw new TimeoutException("等待 WinPilot 退出超时，未替换程序文件");
    }
    catch (ArgumentException) { }
    UpdatePackage.ReplaceFiles(packageRoot, targetDirectory);
    using var restartedProcess = Process.Start(new ProcessStartInfo(restartPath) { Arguments = "--update-completed", UseShellExecute = true, WorkingDirectory = targetDirectory });
    if (restartedProcess is null) throw new InvalidOperationException("程序文件已更新，但无法重新启动 WinPilot");
}
catch (Exception exception)
{
    NativeMethods.ShowError(exception.Message);
}
finally
{
    if (cleanupDirectory is not null)
    {
        try { StartCleanupHelper(cleanupDirectory); } catch { }
    }
}

static Dictionary<string, string> ParseArguments(string[] values) => values.Chunk(2).ToDictionary(pair => pair[0].TrimStart('-'), pair => pair.Length == 2 ? pair[1] : throw new ArgumentException("更新参数无效"), StringComparer.OrdinalIgnoreCase);
static string GetRequired(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"缺少更新参数：{key}");
static void StartCleanupHelper(string directory)
{
    using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{directory}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
}

static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);

    public static void ShowError(string message) => MessageBox(0, message, "WinPilot 更新失败", 0x10);
}
