using WinPilot.Core;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

public sealed class NotificationService : INotificationService, IDisposable
{
    private readonly PortableDataPaths paths;
    private readonly ILogger<NotificationService> logger;
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly WebhookNotificationChannel webhook;
    private readonly WorkWeixinNotificationChannel workWeixin;
    private readonly FeishuNotificationChannel feishu;
    private readonly OneBotNotificationChannel oneBot;
    private readonly QQBotNotificationChannel qqbot;

    public NotificationService(PortableDataPaths paths, ILogger<NotificationService> logger)
    {
        this.paths = paths;
        this.logger = logger;
        webhook = new(httpClient);
        workWeixin = new(httpClient);
        feishu = new(httpClient);
        oneBot = new(httpClient);
        qqbot = new(httpClient);
    }

    public async Task<IReadOnlyList<NotificationChannelResult>> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default,
        string? channel = null,
        bool ignoreChannelEnabled = false)
    {
        var settings = WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).Notifications;
        var channels = new List<Task<NotificationChannelResult>>();
        if (IsSelected(channel, NotificationChannelCatalog.Webhook) && (ignoreChannelEnabled || settings.WebhookEnabled))
            channels.Add(SendChannel(NotificationChannelCatalog.Webhook, () => webhook.SendAsync(settings, message, cancellationToken), cancellationToken));
        if (IsSelected(channel, NotificationChannelCatalog.Telegram) && (ignoreChannelEnabled || settings.TelegramEnabled))
            channels.Add(SendChannel(NotificationChannelCatalog.Telegram, () => TelegramNotificationChannel.SendAsync(settings, message, cancellationToken), cancellationToken));
        if (IsSelected(channel, NotificationChannelCatalog.WorkWeixin) && (ignoreChannelEnabled || settings.WorkWeixinEnabled))
            channels.Add(SendChannel(NotificationChannelCatalog.WorkWeixin, () => workWeixin.SendAsync(settings, message, cancellationToken), cancellationToken));
        if (IsSelected(channel, NotificationChannelCatalog.Windows) && (ignoreChannelEnabled || settings.WindowsEnabled))
            channels.Insert(0, SendChannel(NotificationChannelCatalog.Windows, () => WindowsNotificationChannel.SendAsync(message, cancellationToken), cancellationToken));
        if (IsSelected(channel, NotificationChannelCatalog.Feishu) && (ignoreChannelEnabled || settings.FeishuEnabled))
            channels.Add(SendChannel(NotificationChannelCatalog.Feishu, () => feishu.SendAsync(settings, message, cancellationToken), cancellationToken));
        if (IsSelected(channel, NotificationChannelCatalog.OneBot) && (ignoreChannelEnabled || settings.OneBotEnabled))
            channels.Add(SendChannel(NotificationChannelCatalog.OneBot, () => oneBot.SendAsync(settings, message, cancellationToken), cancellationToken));
        if (IsSelected(channel, NotificationChannelCatalog.QQBot) && (ignoreChannelEnabled || settings.QQBotEnabled))
            channels.Add(SendChannel(NotificationChannelCatalog.QQBot, () => qqbot.SendAsync(settings, message, cancellationToken), cancellationToken));
        return await Task.WhenAll(channels);
    }

    private static bool IsSelected(string? selected, string channel) =>
        string.IsNullOrWhiteSpace(selected) || string.Equals(selected, channel, StringComparison.OrdinalIgnoreCase);

    private async Task<NotificationChannelResult> SendChannel(string id, Func<Task> send, CancellationToken cancellationToken)
    {
        var name = NotificationChannelCatalog.GetDisplayName(id);
        try
        {
            await send();
            return new(name, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var reason = exception switch
            {
                HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
                OperationCanceledException => "请求超时",
                HttpRequestException => "网络请求失败",
                UnauthorizedAccessException => "没有读取图片或发送通知的权限",
                FileNotFoundException or DirectoryNotFoundException => "通知图片不存在",
                InvalidOperationException => "配置不完整或渠道接口返回失败",
                _ => exception.GetType().Name
            };
            logger.LogError("通知渠道 {Channel} 发送失败：{Reason}", name, reason);
            return new(name, false, "发送失败");
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
        qqbot.Dispose();
        feishu.Dispose();
    }
}
