namespace WinPilot.Core.Tasks;

public enum TaskTriggerType { Cron, WinPilotStartup, ProcessStarted, ProcessExited, Extension }
public enum TaskActionType { StartProgram, StartTag, CloseProcess, Delay, Keyboard, Shell, SendNotification, Extension }

public sealed record TaskTrigger(TaskTriggerType Type, string? Cron = null, IReadOnlyList<string>? ProcessPaths = null, string? PluginId = null, string? TriggerId = null, IReadOnlyDictionary<string, object?>? Parameters = null);
public sealed record TaskAction(TaskActionType Type, string? ProgramPath = null, string? Arguments = null, string? WorkingDirectory = null, bool RunElevated = false, bool Silent = false, string? TagName = null, string? ProcessName = null, bool Force = false, int DelaySeconds = 0, IReadOnlyList<string>? Keys = null, string? Shell = null, string? Command = null, string? NotificationTitle = null, string? NotificationContent = null, string? NotificationChannel = null, string? PluginId = null, string? ActionId = null, IReadOnlyDictionary<string, object?>? Parameters = null, bool ContinueOnFailure = false, bool CurrentUserOnly = false);
public sealed record WinPilotTask(string Name, bool Enabled, TaskTrigger Trigger, IReadOnlyList<TaskAction> Actions);
