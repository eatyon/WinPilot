namespace WinPilot.Features.Notifications;

public static class NotificationChannelCatalog
{
    public const string Windows = "Windows";
    public const string Webhook = "Webhook";
    public const string Telegram = "Telegram";
    public const string WorkWeixin = "WorkWeixin";
    public const string Feishu = "Feishu";
    public const string OneBot = "OneBot";
    public const string QQBot = "QQBot";
    public const string AllEnabled = "all-enabled";
    public const string EnabledDisplayName = "已启用渠道";

    public static IReadOnlyList<NotificationChannelDefinition> All { get; } = Array.AsReadOnly<NotificationChannelDefinition>(
    [
        new(Windows, "Windows 通知"),
        new(Webhook, "Webhook"),
        new(Telegram, "Telegram"),
        new(WorkWeixin, "企业微信"),
        new(Feishu, "飞书"),
        new(OneBot, "OneBot"),
        new(QQBot, "QQBot")
    ]);

    public static string GetDisplayName(string id) =>
        All.FirstOrDefault(channel => channel.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;
}

public sealed record NotificationChannelDefinition(string Id, string DisplayName);
