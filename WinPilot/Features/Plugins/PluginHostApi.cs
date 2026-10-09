using System.Diagnostics;
using WinPilot.Core.Programs;
using WinPilot.Features.Terminal;
using WinPilot.Features.Desktop;
using WinPilot.Features.Security;
using WinPilot.Features.Notifications;
using WinPilot.Core.Notifications;
using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed class PluginHostActionApi(IProgramManager programs, KeyboardInputService keyboard, TerminalSessionManager terminals, INotificationService notifications, ILogger<PluginHostActionApi> logger) : IPluginHostActionApi
{
    public async Task<PluginHostActionResult> ExecuteAsync(PluginHostActionStep step, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug("开始执行插件请求的主程序操作，操作类型：{ActionType}", step.Type);
            var values = step.Parameters;
            switch (step.Type)
            {
                case PluginHostActionType.StartProgram:
                    programs.Launch(new(GetString(values, "programPath") ?? throw new InvalidOperationException("缺少 programPath"), GetString(values, "arguments") ?? "", GetString(values, "workingDirectory") ?? "", GetBool(values, "runElevated"), GetBool(values, "silent", true))); break;
                case PluginHostActionType.Delay:
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, GetInt(values, "delaySeconds"))), cancellationToken); break;
                case PluginHostActionType.Keyboard: keyboard.Send(GetStrings(values, "keys")); break;
                case PluginHostActionType.Shell:
                    await terminals.ExecuteTaskCommandAsync(GetString(values, "shell") ?? "cmd", GetString(values, "command") ?? "", cancellationToken); break;
                case PluginHostActionType.SendNotification:
                    if (values.TryGetValue("image", out var imageValue) && imageValue is not null && imageValue is not PluginImageContent)
                        throw new InvalidOperationException("image 必须是 PluginImageContent");
                    await PluginNotificationDelivery.SendAsync(notifications, GetString(values, "content"),
                        imageValue as PluginImageContent,
                        GetString(values, "channel"), cancellationToken); break;
                default: throw new InvalidOperationException("Host Action 类型不支持");
            }
            logger.LogDebug("插件请求的主程序操作已完成，操作类型：{ActionType}", step.Type);
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "执行插件请求的主程序操作失败，操作类型：{ActionType}", step.Type);
            return new(false, exception.Message);
        }
    }
    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) => values.TryGetValue(key, out var value) ? Convert.ToString(value) : null;
    private static bool GetBool(IReadOnlyDictionary<string, object?> values, string key, bool defaultValue = false) => values.TryGetValue(key, out var value) ? Convert.ToBoolean(value) : defaultValue;
    private static int GetInt(IReadOnlyDictionary<string, object?> values, string key) => values.TryGetValue(key, out var value) ? Convert.ToInt32(value) : 0;
    private static IReadOnlyList<string> GetStrings(IReadOnlyDictionary<string, object?> values, string key) => values.TryGetValue(key, out var value) && value is IEnumerable<string> strings ? strings.ToArray() : [];

}

internal static class PluginNotificationDelivery
{
    internal static async Task SendAsync(INotificationService notifications, string? content, PluginImageContent? image, string? channel, CancellationToken token, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(content) && image is null) throw new InvalidOperationException("请输入通知内容或通知图片");
        var imagePath = await WriteImageAsync(image, token);
        try
        {
            var results = await notifications.SendAsync(new NotificationMessage(title ?? "", content ?? "", imagePath), token,
                string.IsNullOrWhiteSpace(channel) ? null : channel.Trim(), !string.IsNullOrWhiteSpace(channel));
            if (results.Count == 0) throw new InvalidOperationException("没有可用的通知渠道");
            var failed = results.Where(result => !result.Succeeded).Select(result => result.Channel).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (failed.Length > 0) throw new InvalidOperationException($"通知发送失败：{string.Join('、', failed)}");
            if (imagePath is not null) await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        finally
        {
            if (imagePath is not null) try { File.Delete(imagePath); } catch { }
        }
    }

    internal static async Task<string?> WriteImageAsync(PluginImageContent? image, CancellationToken token)
    {
        if (image is null) return null;
        if (image.Content.Length == 0 || image.Content.Length > 10 * 1024 * 1024) throw new InvalidOperationException("通知图片大小必须在 1 字节到 10 MB 之间");
        var extension = image.ContentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            _ => throw new InvalidOperationException("通知图片仅支持 PNG 或 JPEG")
        };
        var path = Path.Combine(Path.GetTempPath(), $"WinPilot-notification-{Guid.NewGuid():N}{extension}");
        try { await File.WriteAllBytesAsync(path, image.Content, token); return path; }
        catch { try { File.Delete(path); } catch { } throw; }
    }
}

public sealed class PluginProcessApi : IPluginProcessApi
{
    public bool IsRunning(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        if (string.IsNullOrWhiteSpace(name)) return false;
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

public sealed class PluginHostSecurityApi(SafeModeService safeMode) : IPluginHostSecurityApi
{
    public bool SafeModeEnabled => safeMode.Enabled;
}
