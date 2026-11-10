using Cronos;
using WinPilot.Core.Input;
using WinPilot.Core.Tasks;
using WinPilot.Features.Desktop;
using WinPilot.Features.Notifications;
using WinPilot.Features.Programs;

namespace WinPilot.Features.Tasks;

/// <summary>
/// 统一任务定义的规范化和校验规则。
/// </summary>
internal static class TaskDefinitionValidator
{
    internal static WinPilotTask Normalize(WinPilotTask task) =>
        task with
        {
            Trigger = task.Trigger with
            {
                Cron = task.Trigger.Cron ?? "",
                Keys = KeyboardInputService.OrderKeyNames(task.Trigger.Keys),
                ProcessPaths = (task.Trigger.ProcessPaths ?? [])
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            },
            Actions = task.Actions
                .Select(action =>
                    action with
                    {
                        ProgramPath = action.ProgramPath ?? "",
                        Arguments = action.Arguments ?? "",
                        WorkingDirectory = action.WorkingDirectory ?? "",
                        TagName = action.TagName ?? "",
                        ProcessName = action.ProcessName ?? "",
                        Keys = action.Type == TaskActionType.Keyboard
                            ? KeyboardInputService.OrderKeyNames(action.Keys) ?? []
                            : action.Keys ?? [],
                        Shell = action.Shell ?? "",
                        Command = action.Command ?? "",
                        NotificationContent = action.NotificationContent ?? "",
                        NotificationChannel = action.NotificationChannel ?? ""
                    })
                .ToArray()
        };

    /// <summary>
    /// 校验定义结构并解析定时触发表达式，不检查程序文件的可用性。
    /// </summary>
    internal static CronExpression? Validate(WinPilotTask task)
    {
        CronExpression? cron = null;
        ValidateName(task.Name);
        if (!Enum.IsDefined(task.Trigger.Type))
        {
            throw new TaskValidationException("trigger.type", "任务类型无效");
        }
        for (var index = 0; index < task.Actions.Count; index++)
        {
            if (!Enum.IsDefined(task.Actions[index].Type))
            {
                throw new TaskValidationException($"actions[{index}].type", "任务类型无效");
            }
        }
        if (task.Actions.Count == 0)
        {
            throw new TaskValidationException("actions", "至少需要一个动作");
        }
        if (task.Trigger.Type == TaskTriggerType.Cron)
        {
            if (string.IsNullOrWhiteSpace(task.Trigger.Cron))
            {
                throw new TaskValidationException("trigger.cron", "请输入 Cron 表达式");
            }
            try
            {
                cron = TaskCronExpression.Parse(task.Trigger.Cron);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new TaskValidationException("trigger.cron", "Cron 表达式格式无效", exception);
            }
        }
        else if (task.Trigger.Type == TaskTriggerType.Keyboard)
        {
            ValidateKeys(task.Trigger.Keys, "trigger.keys");
        }
        else if (task.Trigger.Type == TaskTriggerType.Extension)
        {
            if (string.IsNullOrWhiteSpace(task.Trigger.PluginId))
            {
                throw new TaskValidationException("trigger.pluginId", "请选择插件和触发条件");
            }
            if (string.IsNullOrWhiteSpace(task.Trigger.TriggerId))
            {
                throw new TaskValidationException("trigger.triggerId", "请选择插件和触发条件");
            }
        }
        else if (task.Trigger.Type == TaskTriggerType.Process)
        {
            if (task.Trigger.Event is null)
            {
                throw new TaskValidationException("trigger.event", "请选择事件");
            }
            if (!Enum.IsDefined(task.Trigger.Event.Value))
            {
                throw new TaskValidationException("trigger.event", "程序事件类型无效");
            }
            var paths = task.Trigger.ProcessPaths ?? [];
            if (paths.Count == 0)
            {
                throw new TaskValidationException("trigger.processPaths", "请至少选择一个程序绝对路径");
            }
            if (paths.Any(path => !Path.IsPathFullyQualified(path)))
            {
                throw new TaskValidationException("trigger.processPaths", "程序路径必须是绝对路径");
            }
        }

        for (var index = 0; index < task.Actions.Count; index++)
        {
            var action = task.Actions[index];
            switch (action.Type)
            {
                case TaskActionType.StartProgram when string.IsNullOrWhiteSpace(action.ProgramPath):
                    throw new TaskValidationException($"actions[{index}].programPath", "请选择要启动的程序");
                case TaskActionType.StartTag when string.IsNullOrWhiteSpace(action.TagName):
                    throw new TaskValidationException($"actions[{index}].tagName", "请选择快捷标签");
                case TaskActionType.EndProcess when string.IsNullOrWhiteSpace(action.ProcessName):
                    throw new TaskValidationException($"actions[{index}].processName", "请输入进程名");
                case TaskActionType.Delay when action.DelaySeconds <= 0:
                    throw new TaskValidationException($"actions[{index}].delaySeconds", "请输入有效的延迟秒数");
                case TaskActionType.Delay when action.DelaySeconds > 86400:
                    throw new TaskValidationException($"actions[{index}].delaySeconds", "延迟时间不能超过 86400 秒（24 小时）");
                case TaskActionType.Keyboard:
                    ValidateKeys(action.Keys, $"actions[{index}].keys");
                    break;
                case TaskActionType.Extension when string.IsNullOrWhiteSpace(action.PluginId):
                    throw new TaskValidationException($"actions[{index}].pluginId", "请选择插件和动作");
                case TaskActionType.Extension when string.IsNullOrWhiteSpace(action.ActionId):
                    throw new TaskValidationException($"actions[{index}].actionId", "请选择插件和动作");
                case TaskActionType.Shell when !IsSupportedShell(action.Shell):
                    throw new TaskValidationException($"actions[{index}].shell", "Shell 仅支持 CMD 或 PowerShell");
                case TaskActionType.Shell when string.IsNullOrWhiteSpace(action.Command):
                    throw new TaskValidationException($"actions[{index}].command", "请输入 Shell 命令");
                case TaskActionType.SendNotification when string.IsNullOrWhiteSpace(action.NotificationContent):
                    throw new TaskValidationException($"actions[{index}].notificationContent", "请输入通知内容");
                case TaskActionType.SendNotification:
                    try
                    {
                        NotificationChannelCatalog.RequireChannel(action.NotificationChannel);
                    }
                    catch (InvalidOperationException exception)
                    {
                        throw new TaskValidationException($"actions[{index}].notificationChannel", exception.Message, exception);
                    }
                    break;
                case TaskActionType.TaskState when string.IsNullOrWhiteSpace(action.TaskName):
                    throw new TaskValidationException($"actions[{index}].taskName", "请选择任务");
                case TaskActionType.TaskState when action.State is null:
                    throw new TaskValidationException($"actions[{index}].state", "请选择状态");
                case TaskActionType.TaskState when action.State is { } state && !Enum.IsDefined(state):
                    throw new TaskValidationException($"actions[{index}].state", "任务状态无效");
            }
        }
        return cron;
    }

    private static bool IsSupportedShell(string? shell) =>
        shell is not null
        && (shell.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                || shell.Equals("powershell", StringComparison.OrdinalIgnoreCase));

    private static void ValidateKeys(IReadOnlyList<string>? keys, string field)
    {
        if (keys is null || keys.Count == 0)
        {
            throw new TaskValidationException(field, "请选择至少一个按键");
        }
        if (keys.Count > 3)
        {
            throw new TaskValidationException(field, "最多选择 3 个按键");
        }
        var parsed = new HashSet<KeyboardKey>();
        foreach (var name in keys)
        {
            if (!Enum.TryParse<KeyboardKey>(name, true, out var key)
                || !string.Equals(Enum.GetName(key), name, StringComparison.OrdinalIgnoreCase))
            {
                throw new TaskValidationException(field, "包含不支持的按键");
            }
            if (!parsed.Add(key))
            {
                throw new TaskValidationException(field, "按键不能重复");
            }
        }
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.')
            || name.EndsWith(' '))
        {
            throw new TaskValidationException("name", "任务名称无效");
        }
    }

    /// <summary>
    /// 创建和更新时检查程序路径与工作目录，加载已有任务时不执行。
    /// </summary>
    internal static void ValidateProgramConfigurations(WinPilotTask task)
    {
        if (task.Trigger.Type == TaskTriggerType.Process)
        {
            foreach (var path in task.Trigger.ProcessPaths ?? [])
            {
                if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    throw new TaskValidationException("trigger.processPaths", "程序事件仅支持 EXE 文件");
                }
                try
                {
                    ProgramLaunchValidator.ValidateProgramPath(path);
                }
                catch (InvalidOperationException exception)
                {
                    throw new TaskValidationException("trigger.processPaths", exception.Message, exception);
                }
            }
        }

        for (var index = 0; index < task.Actions.Count; index++)
        {
            var action = task.Actions[index];
            if (action.Type != TaskActionType.StartProgram)
            {
                continue;
            }
            try
            {
                ProgramLaunchValidator.ValidateProgramPath(action.ProgramPath);
            }
            catch (InvalidOperationException exception)
            {
                throw new TaskValidationException($"actions[{index}].programPath", exception.Message, exception);
            }
            try
            {
                ProgramLaunchValidator.ValidateWorkingDirectory(action.WorkingDirectory);
            }
            catch (InvalidOperationException exception)
            {
                throw new TaskValidationException($"actions[{index}].workingDirectory", exception.Message, exception);
            }
        }
    }
}
