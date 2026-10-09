namespace WinPilot.Core;

public sealed record CommandBotSettings
{
    public bool TelegramEnabled { get; init; }
    public string TelegramPrefix { get; init; } = "";
    public bool FeishuEnabled { get; init; }
    public string FeishuPrefix { get; init; } = "";
    public bool OneBotEnabled { get; init; }
    public string OneBotPrefix { get; init; } = "";
    public string OneBotWebSocketUrl { get; init; } = "";
    public string OneBotToken { get; init; } = "";
    public bool QQBotEnabled { get; init; }
    public string QQBotPrefix { get; init; } = "";
}
