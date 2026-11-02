using WinPilot.Core;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

/// <summary>
/// 按指定渠道和最新设置发送通知，按需创建渠道资源，并统一处理发送结果与脱敏日志。
/// </summary>
public sealed class NotificationService : INotificationService, IDisposable
{
    private readonly PortableDataPaths paths;
    private readonly ILogger<NotificationService> logger;
    private readonly object resourcesGate = new();
    private HttpClient? httpClient;
    private WebhookNotificationChannel? webhook;
    private WorkWeixinNotificationChannel? workWeixin;
    private FeishuNotificationChannel? feishu;
    private OneBotNotificationChannel? oneBot;
    private QQBotNotificationChannel? qqbot;
    private bool disposed;

    private WebhookNotificationChannel Webhook =>
        GetChannel(ref webhook, static client => new WebhookNotificationChannel(client));
    private WorkWeixinNotificationChannel WorkWeixin =>
        GetChannel(ref workWeixin, static client => new WorkWeixinNotificationChannel(client));
    private FeishuNotificationChannel Feishu =>
        GetChannel(ref feishu, static client => new FeishuNotificationChannel(client));
    private OneBotNotificationChannel OneBot =>
        GetChannel(ref oneBot, static client => new OneBotNotificationChannel(client));
    private QQBotNotificationChannel QQBot => GetChannel(ref qqbot, static client => new QQBotNotificationChannel(client));

    public NotificationService(PortableDataPaths paths, ILogger<NotificationService> logger)
    {
        this.paths = paths;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<NotificationChannelResult>> SendAsync(
        NotificationMessage message,
        string? channel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = NotificationChannelCatalog.RequireChannel(channel);
        var settings = WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).Notifications;
        var secrets = NotificationFailureDiagnostics.Secrets(settings);
        var result = await SendChannel(
            id,
            () =>
                id switch
                {
                    NotificationChannelCatalog.Windows => WindowsNotificationChannel.SendAsync(message, cancellationToken),
                    NotificationChannelCatalog.Webhook => Webhook.SendAsync(settings, message, cancellationToken),
                    NotificationChannelCatalog.Telegram => TelegramNotificationChannel.SendAsync(settings, message, cancellationToken),
                    NotificationChannelCatalog.WorkWeixin => WorkWeixin.SendAsync(settings, message, cancellationToken),
                    NotificationChannelCatalog.Feishu => Feishu.SendAsync(settings, message, cancellationToken),
                    NotificationChannelCatalog.OneBot => OneBot.SendAsync(settings, message, cancellationToken),
                    NotificationChannelCatalog.QQBot => QQBot.SendAsync(settings, message, cancellationToken),
                    _ => throw new InvalidOperationException("通知渠道无效")
                },
            cancellationToken,
            secrets);
        return [result];
    }

    // 只有实际使用渠道时才创建实例；需要 HTTP 的渠道复用同一个客户端。
    private T GetChannel<T>(ref T? channel, Func<HttpClient, T> create) where T : class
    {
        lock (resourcesGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return channel ??= create(httpClient ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        }
    }

    internal async Task<NotificationChannelResult> SendChannel(
        string id,
        Func<Task> send,
        CancellationToken cancellationToken,
        IEnumerable<string>? secrets = null)
    {
        var name = NotificationChannelCatalog.GetDisplayName(id);
        try
        {
            await send();
            return new(name, true);
        }
        // 主动取消交回调用方；渠道超时等其他异常转换成失败结果。
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
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
            logger.LogError(
                NotificationFailureDiagnostics.Redact(exception, secrets),
                "通知渠道 {Channel} 发送失败：{Reason}",
                name,
                reason);
            return new(name, false, "发送失败");
        }
    }

    public void Dispose()
    {
        HttpClient? client;
        QQBotNotificationChannel? qqbotChannel;
        FeishuNotificationChannel? feishuChannel;
        lock (resourcesGate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            client = httpClient;
            qqbotChannel = qqbot;
            feishuChannel = feishu;
        }
        client?.Dispose();
        qqbotChannel?.Dispose();
        feishuChannel?.Dispose();
    }
}
