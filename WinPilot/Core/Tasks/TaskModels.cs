namespace WinPilot.Core.Tasks;

public enum TaskTriggerType { Cron, WinPilotStartup, Process, Extension, Keyboard }
public enum TaskProcessEventType { Started, Exited }
public enum TaskActionType { StartProgram, StartTag, EndProcess, Delay, Keyboard, Shell, SendNotification, Extension, TaskState }
public enum TaskStateOperation { Toggle, Enable, Disable }

public sealed record TaskTrigger(TaskTriggerType Type, string? Cron = null, IReadOnlyList<string>? ProcessPaths = null, string? PluginId = null, string? TriggerId = null, IReadOnlyDictionary<string, object?>? Parameters = null, TaskProcessEventType? Event = null, IReadOnlyList<string>? Keys = null);
public sealed record TaskAction(TaskActionType Type, string? ProgramPath = null, string? Arguments = null, string? WorkingDirectory = null, bool RunElevated = false, bool Silent = false, string? TagName = null, string? ProcessName = null, int DelaySeconds = 0, IReadOnlyList<string>? Keys = null, string? Shell = null, string? Command = null, string? NotificationTitle = null, string? NotificationContent = null, string? NotificationChannel = null, string? PluginId = null, string? ActionId = null, IReadOnlyDictionary<string, object?>? Parameters = null, bool ContinueOnFailure = false, bool CurrentUserOnly = false, string? TaskName = null, TaskStateOperation? State = null);
public sealed record WinPilotTask(string Name, bool Enabled, TaskTrigger Trigger, IReadOnlyList<TaskAction> Actions);
