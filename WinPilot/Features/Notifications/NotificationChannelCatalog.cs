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

    internal static string RequireChannel(string? channel)
    {
        var id = channel?.Trim();
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("请选择发送渠道");
        return Find(id)?.Id
            ?? throw new InvalidOperationException("通知渠道无效");
    }

    internal static NotificationChannelDefinition? Find(string id) =>
        All.FirstOrDefault(channel => channel.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static string GetDisplayName(string id) =>
        Find(id)?.DisplayName ?? id;
}

public sealed record NotificationChannelDefinition(string Id, string DisplayName);
