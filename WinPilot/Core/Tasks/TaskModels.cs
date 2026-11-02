namespace WinPilot.Core.Tasks;

public enum TaskTriggerType
{
    Cron,
    WinPilotStartup,
    Process,
    Extension,
    Keyboard
}

public enum TaskProcessEventType
{
    Started,
    Exited
}

public enum TaskActionType
{
    StartProgram,
    StartTag,
    EndProcess,
    Delay,
    Keyboard,
    Shell,
    SendNotification,
    Extension,
    TaskState
}

public enum TaskStateOperation
{
    Toggle,
    Enable,
    Disable
}

/// <summary>
/// 任务触发条件；Type 决定使用 Cron、程序路径、按键或插件参数中的哪组字段。
/// </summary>
public sealed record TaskTrigger(
    TaskTriggerType Type,
    string? Cron = null,
    IReadOnlyList<string>? ProcessPaths = null,
    string? PluginId = null,
    string? TriggerId = null,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    TaskProcessEventType? Event = null,
    IReadOnlyList<string>? Keys = null);

/// <summary>
/// 一个任务动作；只解释 Type 对应的字段，ContinueOnFailure 决定失败后是否继续后续动作。
/// </summary>
public sealed record TaskAction(
    TaskActionType Type,
    string? ProgramPath = null,
    string? Arguments = null,
    string? WorkingDirectory = null,
    bool RunElevated = false,
    bool Silent = false,
    string? TagName = null,
    string? ProcessName = null,
    int DelaySeconds = 0,
    IReadOnlyList<string>? Keys = null,
    string? Shell = null,
    string? Command = null,
    string? NotificationContent = null,
    string? NotificationChannel = null,
    string? PluginId = null,
    string? ActionId = null,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    bool ContinueOnFailure = false,
    bool CurrentUserOnly = false,
    string? TaskName = null,
    TaskStateOperation? State = null);

/// <summary>
/// 可持久化的任务定义；动作按列表顺序执行，运行状态另由任务服务管理。
/// </summary>
public sealed record WinPilotTask(
    string Name,
    bool Enabled,
    TaskTrigger Trigger,
    IReadOnlyList<TaskAction> Actions);
