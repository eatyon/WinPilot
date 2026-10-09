namespace WinPilot.Core;

public sealed record NotificationSettings
{
    public bool WindowsEnabled { get; init; }
    public bool WebhookEnabled { get; init; }
    public string WebhookUrl { get; init; } = "";
    public string WebhookSendTo { get; init; } = "";
    public bool TelegramEnabled { get; init; }
    public string TelegramBotToken { get; init; } = "";
    public string TelegramChatId { get; init; } = "";
    public string TelegramApiBaseUrl { get; init; } = "";
    public string TelegramProxyUrl { get; init; } = "http://127.0.0.1:7890";
    public bool TelegramProxyEnabled { get; init; }
    public bool WorkWeixinEnabled { get; init; }
    public string WorkWeixinWebhookUrl { get; init; } = "";
    public bool FeishuEnabled { get; init; }
    public string FeishuWebhookUrl { get; init; } = "";
    public string FeishuAppId { get; init; } = "";
    public string FeishuAppSecret { get; init; } = "";
    public bool OneBotEnabled { get; init; }
    public string OneBotEndpoint { get; init; } = "";
    public string OneBotUserId { get; init; } = "";
    public string OneBotGroupId { get; init; } = "";
    public string OneBotToken { get; init; } = "";
    public string OneBotSendTarget { get; init; } = "auto";
    public bool QQBotEnabled { get; init; }
    public string QQBotAppId { get; init; } = "";
    public string QQBotClientSecret { get; init; } = "";
    public string QQBotOpenId { get; init; } = "";
    public string QQBotGroupOpenId { get; init; } = "";
    public string QQBotSendTarget { get; init; } = "auto";
    public string QQBotMessageFormat { get; init; } = "text";
}
