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
        var actionName = step.Type switch
        {
            PluginHostActionType.StartProgram => "启动程序",
            PluginHostActionType.Delay => "延迟",
            PluginHostActionType.Keyboard => "发送按键",
            PluginHostActionType.Shell => "执行终端命令",
            PluginHostActionType.SendNotification => "发送通知",
            _ => step.Type.ToString()
        };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug("开始执行插件请求的主程序操作，操作类型：{ActionName}", actionName);
            if (step.Type == PluginHostActionType.StartProgram && !step.Parameters.ContainsKey("programPath"))
                throw new InvalidOperationException("缺少 programPath");
            var (action, image) = PluginHostActionConverter.Convert(step, defaultSilent: true, defaultShell: "cmd", includeNotificationTitle: false);
            switch (step.Type)
            {
                case PluginHostActionType.StartProgram:
                    programs.Launch(new(action.ProgramPath ?? throw new InvalidOperationException("缺少 programPath"), action.Arguments ?? "", action.WorkingDirectory ?? "", action.RunElevated, action.Silent)); break;
                case PluginHostActionType.Delay:
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, action.DelaySeconds)), cancellationToken); break;
                case PluginHostActionType.Keyboard: keyboard.Send(action.Keys); break;
                case PluginHostActionType.Shell:
                    await terminals.ExecuteTaskCommandAsync(action.Shell ?? "", action.Command ?? "", cancellationToken); break;
                case PluginHostActionType.SendNotification:
                    var channel = action.NotificationChannel;
                    PluginNotificationImageValidator.ValidateImageSize(image);
                    await NotificationDelivery.SendAsync(notifications, new NotificationMessage("", action.NotificationContent ?? ""), cancellationToken,
                        channel: string.IsNullOrWhiteSpace(channel) ? null : channel.Trim(),
                        ignoreChannelEnabled: !string.IsNullOrWhiteSpace(channel),
                        imageContent: image?.Content, imageContentType: image?.ContentType); break;
                default: throw new InvalidOperationException("Host Action 类型不支持");
            }
            logger.LogDebug("插件请求的主程序操作已完成，操作类型：{ActionName}", actionName);
            return new(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "执行插件请求的主程序操作失败，操作类型：{ActionName}", actionName);
            return new(false, exception.Message);
        }
    }
}

internal static class PluginNotificationImageValidator
{
    internal static void ValidateImageSize(PluginImageContent? image)
    {
        if (image is not null && (image.Content.Length == 0 || image.Content.Length > NotificationDelivery.MaxImageSizeBytes))
            throw new InvalidOperationException("通知图片大小必须在 1 字节到 10 MB 之间");
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
