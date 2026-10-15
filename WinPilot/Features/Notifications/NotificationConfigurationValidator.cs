using WinPilot.Core;

namespace WinPilot.Features.Notifications;

internal static class NotificationConfigurationValidator
{
    internal static void Validate(NotificationSettings settings)
    {
        ValidateSendTarget(settings.OneBotSendTarget, "OneBot");
        ValidateSendTarget(settings.QQBotSendTarget, "QQBot");
        ValidateQQBotMessageFormat(settings.QQBotMessageFormat);
    }

    internal static void ValidateSendTarget(string? target, string channel)
    {
        if (target is not ("auto" or "private" or "group"))
            throw new InvalidOperationException($"{channel} 发送目标无效");
    }

    internal static void ValidateQQBotMessageFormat(string? format)
    {
        if (format is not ("text" or "markdown"))
            throw new InvalidOperationException("QQBot 消息格式无效");
    }

    internal static void Validate(SecurityNotificationSettings settings)
    {
        if ((settings.Events ?? []).Any(type => !Enum.IsDefined(type)))
            throw new InvalidOperationException("安全事件类型无效");
        var channel = (settings.Channel ?? "").Trim();
        if (channel.Length > 0 && !channel.Equals(NotificationChannelCatalog.AllEnabled, StringComparison.OrdinalIgnoreCase)
            && !NotificationChannelCatalog.All.Any(item => item.Id.Equals(channel, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("安全通知渠道无效");
    }
}
