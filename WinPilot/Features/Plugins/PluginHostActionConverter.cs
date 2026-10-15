using WinPilot.Core.Tasks;
using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

internal static class PluginHostActionConverter
{
    internal static (TaskAction Action, PluginImageContent? Image) Convert(PluginHostActionStep step,
        bool defaultSilent, string? defaultShell, bool includeNotificationTitle)
    {
        var values = step.Parameters;
        return step.Type switch
        {
            PluginHostActionType.StartProgram => (new TaskAction(TaskActionType.StartProgram,
                ProgramPath: GetString(values, "programPath"), Arguments: GetString(values, "arguments"),
                WorkingDirectory: GetString(values, "workingDirectory"), RunElevated: GetBool(values, "runElevated", false),
                Silent: GetBool(values, "silent", defaultSilent)), null),
            PluginHostActionType.Delay => (new TaskAction(TaskActionType.Delay, DelaySeconds: GetInt(values, "delaySeconds")), null),
            PluginHostActionType.Keyboard => (new TaskAction(TaskActionType.Keyboard, Keys: GetStrings(values, "keys")), null),
            PluginHostActionType.Shell => (new TaskAction(TaskActionType.Shell,
                Shell: GetString(values, "shell") ?? defaultShell, Command: GetString(values, "command")), null),
            PluginHostActionType.SendNotification => ConvertNotification(values, includeNotificationTitle),
            _ => throw new InvalidOperationException("Host Action 类型不支持")
        };
    }

    private static (TaskAction Action, PluginImageContent? Image) ConvertNotification(IReadOnlyDictionary<string, object?> values, bool includeTitle)
    {
        var channel = includeTitle ? GetString(values, "channel") : null;
        values.TryGetValue("image", out var imageValue);
        if (imageValue is not null && imageValue is not PluginImageContent)
            throw new InvalidOperationException("image 必须是 PluginImageContent");
        var title = includeTitle ? GetString(values, "title") ?? "" : "";
        var content = GetString(values, "content") ?? "";
        if (!includeTitle) channel = GetString(values, "channel");
        return (new TaskAction(TaskActionType.SendNotification, NotificationTitle: title, NotificationContent: content,
            NotificationChannel: channel), imageValue as PluginImageContent);
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) ? System.Convert.ToString(value) : null;

    private static bool GetBool(IReadOnlyDictionary<string, object?> values, string key, bool defaultValue)
        => values.TryGetValue(key, out var value) ? System.Convert.ToBoolean(value) : defaultValue;

    private static int GetInt(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) ? System.Convert.ToInt32(value) : 0;

    private static IReadOnlyList<string> GetStrings(IReadOnlyDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) && value is IEnumerable<string> strings ? strings.ToArray() : [];
}
