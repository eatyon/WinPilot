using System.Text.Json;
using WinPilot.Core.Tasks;

namespace WinPilot.Features.Tasks;

public sealed class TaskPersistenceFile
{
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    public TaskTriggerPersistence Trigger { get; init; } = new();
    public IReadOnlyList<TaskActionPersistence> Actions { get; init; } = [];

    public static TaskPersistenceFile From(WinPilotTask task) => new()
    {
        Name = task.Name,
        Enabled = task.Enabled,
        Trigger = TaskTriggerPersistence.From(task.Trigger),
        Actions = task.Actions.Select(TaskActionPersistence.From).ToArray()
    };

    public WinPilotTask ToTask() => new(Name, Enabled, Trigger.ToTrigger(), Actions.Select(action => action.ToAction()).ToArray());
}

public sealed class TaskTriggerPersistence
{
    public string Type { get; init; } = "";
    public string? Cron { get; init; }
    public IReadOnlyList<string>? ProcessPaths { get; init; }
    public string? PluginId { get; init; }
    public string? TriggerId { get; init; }
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    public static TaskTriggerPersistence From(TaskTrigger trigger) => trigger.Type switch
    {
        TaskTriggerType.Cron => new() { Type = trigger.Type.ToString(), Cron = trigger.Cron ?? "" },
        TaskTriggerType.ProcessStarted or TaskTriggerType.ProcessExited => new() { Type = trigger.Type.ToString(), ProcessPaths = trigger.ProcessPaths?.ToArray() ?? [] },
        TaskTriggerType.Extension => new() { Type = trigger.Type.ToString(), PluginId = trigger.PluginId, TriggerId = trigger.TriggerId, Parameters = trigger.Parameters },
        _ => new() { Type = trigger.Type.ToString() }
    };

    public TaskTrigger ToTrigger() => new(ParseEnum<TaskTriggerType>(Type, "触发条件"), Cron, ProcessPaths, PluginId, TriggerId, Parameters);

    internal static T ParseEnum<T>(string value, string fieldName) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)) return parsed;
        throw new JsonException($"{fieldName}类型无效：{value}");
    }
}

public sealed class TaskActionPersistence
{
    public string Type { get; init; } = "";
    public string? ProgramPath { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool? RunElevated { get; init; }
    public bool? Silent { get; init; }
    public string? TagName { get; init; }
    public string? ProcessName { get; init; }
    public bool? Force { get; init; }
    public bool? CurrentUserOnly { get; init; }
    public int? DelaySeconds { get; init; }
    public IReadOnlyList<string>? Keys { get; init; }
    public string? Shell { get; init; }
    public string? Command { get; init; }
    public string? NotificationTitle { get; init; }
    public string? NotificationContent { get; init; }
    public string? NotificationChannel { get; init; }
    public string? PluginId { get; init; }
    public string? ActionId { get; init; }
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }
    public bool? ContinueOnFailure { get; init; }

    public static TaskActionPersistence From(TaskAction action) => action.Type switch
    {
        TaskActionType.StartProgram => new()
        {
            Type = action.Type.ToString(),
            ProgramPath = action.ProgramPath ?? "",
            Arguments = EmptyToNull(action.Arguments),
            WorkingDirectory = EmptyToNull(action.WorkingDirectory),
            RunElevated = action.RunElevated,
            Silent = action.Silent,
            ContinueOnFailure = TrueOrNull(action.ContinueOnFailure)
        },
        TaskActionType.StartTag => new() { Type = action.Type.ToString(), TagName = action.TagName ?? "", ContinueOnFailure = TrueOrNull(action.ContinueOnFailure) },
        TaskActionType.CloseProcess => new() { Type = action.Type.ToString(), ProcessName = action.ProcessName ?? "", Force = action.Force, CurrentUserOnly = TrueOrNull(action.CurrentUserOnly), ContinueOnFailure = TrueOrNull(action.ContinueOnFailure) },
        TaskActionType.Delay => new() { Type = action.Type.ToString(), DelaySeconds = action.DelaySeconds, ContinueOnFailure = TrueOrNull(action.ContinueOnFailure) },
        TaskActionType.Keyboard => new() { Type = action.Type.ToString(), Keys = action.Keys?.ToArray() ?? [], ContinueOnFailure = TrueOrNull(action.ContinueOnFailure) },
        TaskActionType.Shell => new() { Type = action.Type.ToString(), Shell = action.Shell ?? "", Command = action.Command ?? "", ContinueOnFailure = TrueOrNull(action.ContinueOnFailure) },
        TaskActionType.SendNotification or TaskActionType.SendNotificationToChannel => new() { Type = action.Type.ToString(), NotificationTitle = EmptyToNull(action.NotificationTitle), NotificationContent = action.NotificationContent ?? "", NotificationChannel = EmptyToNull(action.NotificationChannel), ContinueOnFailure = TrueOrNull(action.ContinueOnFailure) },
        TaskActionType.Extension => new() { Type = action.Type.ToString(), PluginId = action.PluginId, ActionId = action.ActionId, Parameters = action.Parameters, ContinueOnFailure = TrueOrNull(action.ContinueOnFailure) },
        _ => throw new JsonException($"动作类型无效：{action.Type}")
    };

    public TaskAction ToAction() => new(
        TaskTriggerPersistence.ParseEnum<TaskActionType>(Type, "动作"),
        ProgramPath,
        Arguments,
        WorkingDirectory,
        RunElevated ?? false,
        Silent ?? false,
        TagName,
        ProcessName,
        Force ?? false,
        DelaySeconds ?? 0,
        Keys,
        Shell,
        Command,
        NotificationTitle,
        NotificationContent,
        NotificationChannel,
        PluginId,
        ActionId,
        Parameters,
        ContinueOnFailure ?? false,
        CurrentUserOnly ?? false);

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static bool? TrueOrNull(bool value) => value ? true : null;
}
