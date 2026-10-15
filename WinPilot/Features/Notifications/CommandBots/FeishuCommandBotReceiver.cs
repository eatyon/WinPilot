using WinPilot.Core;

namespace WinPilot.Features.Notifications.CommandBots;

internal sealed class FeishuCommandBotReceiver(ILogger<FeishuCommandBotService> logger, FeishuConnectAsync connect,
    Func<CommandBotStatus> status, Action<CommandBotStatus> publish, Action<CommandBotMessage> receive)
    : CommandBotReceiver(status, publish, receive)
{
    public override async Task RunAsync(WinPilotSettings configuration, CancellationToken token)
    {
        var settings = configuration.Notifications ?? new();
        var retry = new FeishuReconnectPolicy();
        var seen = new Dictionary<string, DateTimeOffset>();
        while (!token.IsCancellationRequested)
        {
            long? connectedAt = null;
            try
            {
                await connect(settings, () =>
                {
                    connectedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    SetStatus(Status with { State = "connected", Error = null });
                }, message =>
                {
                    if (!token.IsCancellationRequested) Receive(message);
                }, seen, config => retry.Config = config, token);
                if (!token.IsCancellationRequested) throw new IOException("连接已关闭");
            }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            catch (FeishuConnectionException exception) when (exception.Permanent)
            {
                logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, NotificationFailureDiagnostics.Secrets(settings)), "飞书命令机器人连接无法恢复");
                SetStatus(Status with { State = "failed", Error = exception.Message });
                return;
            }
            catch (Exception exception)
            {
                var connectedDuration = connectedAt is { } timestamp
                    ? System.Diagnostics.Stopwatch.GetElapsedTime(timestamp) : (TimeSpan?)null;
                if (!retry.TryNextDelay(connectedDuration, Random.Shared.NextDouble(), out var delay))
                {
                    logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, NotificationFailureDiagnostics.Secrets(settings)), "飞书命令机器人重连已停止：次数已达上限");
                    SetStatus(Status with { State = "failed", Error = "飞书自动重连次数已达上限，请检查网络后点击重新连接" });
                    return;
                }
                if (Status.State != "reconnecting") logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, NotificationFailureDiagnostics.Secrets(settings)), "飞书命令机器人连接中断，正在重连");
                SetStatus(Status with { State = "reconnecting", Error = "无法连接飞书，请检查网络和应用配置，正在重连" });
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
    }
}

internal delegate Task FeishuConnectAsync(NotificationSettings settings, Action connected, Action<CommandBotMessage> receive,
    Dictionary<string, DateTimeOffset> seen, Action<FeishuClientConfig> configure, CancellationToken token);

internal sealed class FeishuReconnectPolicy
{
    public FeishuClientConfig Config { get; set; } = new();
    private int attempts;

    public bool TryNextDelay(TimeSpan? connectedDuration, double randomSample, out TimeSpan delay)
    {
        if (connectedDuration >= TimeSpan.FromSeconds(Math.Max(30, Config.PingInterval * 2))) attempts = 0;
        if (Config.ReconnectCount >= 0 && attempts >= Config.ReconnectCount)
        {
            delay = default;
            return false;
        }
        var backoff = Math.Min(2 * Math.Pow(2, Math.Min(attempts, 4)), 30);
        var seconds = attempts == 0 ? backoff : Math.Max(backoff, Config.ReconnectInterval);
        var jitter = attempts == 0 ? Config.ReconnectNonce : seconds * 0.2;
        delay = TimeSpan.FromSeconds(seconds + jitter * Math.Clamp(randomSample, 0, 1));
        if (attempts < int.MaxValue) attempts++;
        return true;
    }
}

internal sealed class FeishuConnectionException(string message, bool permanent) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}
