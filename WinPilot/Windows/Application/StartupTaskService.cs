using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;
using WinPilot.Core;

namespace WinPilot.Windows;
public sealed class StartupTaskService
{
    public const string TaskName = "WinPilot_Autorun";
    private const int DelayedSeconds = 30;
    public void Apply(StartupMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), "开机启动模式无效");
        if (mode == StartupMode.Disabled)
        {
            Disable();
            return;
        }

        var target = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            throw new InvalidOperationException("无法确定当前 WinPilot.exe 路径");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.Name;
        using var service = new TaskService();
        try
        {
            service.RootFolder.DeleteTask(TaskName, false);
        }
        catch (FileNotFoundException)
        {
        }

        using var definition = service.NewTask();
        definition.Settings.Compatibility = TaskCompatibility.V2_3;
        definition.RegistrationInfo.Description = "WinPilot 开机启动任务";
        definition.Principal.UserId = user;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = IsUacDisabledOrBuiltInAdmin() ? TaskRunLevel.LUA : TaskRunLevel.Highest;
        definition.Triggers.Add(new LogonTrigger { UserId = user, Delay = mode == StartupMode.Delayed ? TimeSpan.FromSeconds(DelayedSeconds) : TimeSpan.Zero });
        definition.Actions.Add(new ExecAction("rundll32.exe", $"shell32.dll,ShellExec_RunDLL \"{target}\" --minimized", Path.GetDirectoryName(target)));
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        service.RootFolder.RegisterTaskDefinition(TaskName, definition);
    }

    private static void Disable()
    {
        using var service = new TaskService();
        try
        {
            service.RootFolder.DeleteTask(TaskName, false);
        }
        catch (FileNotFoundException)
        {
        }
    }

    private static bool IsUacDisabledOrBuiltInAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.AccountAdministratorSid) == true)
            return true;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        return key?.GetValue("EnableLUA")is int enableLua && enableLua == 0;
    }
}
