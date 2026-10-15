using System.Text.Json;
using WinPilot.Core;

namespace WinPilot.Features.Notifications.CommandBots;

internal sealed class OneBotCommandBotReceiver(ILogger<OneBotCommandBotService> logger, OneBotConnectAsync connect,
    Func<CommandBotStatus> status, Action<CommandBotStatus> publish, Action<CommandBotMessage> receive)
    : CommandBotReceiver(status, publish, receive)
{
    public override async Task RunAsync(WinPilotSettings configuration, CancellationToken token)
    {
        var options = OneBotCommandBotService.Options(configuration.CommandBots ?? new());
        var settings = configuration.Notifications ?? new();
        var diagnosticSecrets = NotificationFailureDiagnostics.Secrets(settings, configuration.CommandBots);
        var seen = new Dictionary<string, DateTimeOffset>();
        var attempts = 0;
        while (!token.IsCancellationRequested)
        {
            long? connectedAt = null;
            try
            {
                await connect(options, () =>
                {
                    connectedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    SetStatus(Status with { State = "connected", Error = null });
                }, payload =>
                {
                    if (token.IsCancellationRequested) return;
                    var parsed = OneBotCommandBotTransport.ParseMessage(payload);
                    if (parsed is null) return;
                    var (key, message) = parsed.Value;
                    if (!IsAllowed(settings, payload.GetProperty("message_type").GetString(), message)) return;
                    var now = DateTimeOffset.UtcNow;
                    foreach (var id in seen.Where(entry => now - entry.Value > TimeSpan.FromMinutes(5)).Select(entry => entry.Key).ToArray()) seen.Remove(id);
                    if (!seen.TryAdd(key, now)) return;
                    if (seen.Count > 4096) seen.Remove(seen.MinBy(entry => entry.Value).Key);
                    Receive(message);
                }, token);
                if (!token.IsCancellationRequested) throw new IOException();
            }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            catch (OneBotConnectionException exception) when (exception.Permanent)
            {
                logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "OneBot 命令机器人连接无法恢复");
                SetStatus(Status with { State = "failed", Error = exception.Message });
                return;
            }
            catch (Exception exception)
            {
                if (connectedAt is { } timestamp && System.Diagnostics.Stopwatch.GetElapsedTime(timestamp) >= TimeSpan.FromMinutes(2)) attempts = 0;
                if (attempts >= 100)
                {
                    logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "OneBot 命令机器人重连已停止：次数已达上限");
                    SetStatus(Status with { State = "failed", Error = "OneBot 自动重连次数已达上限，请检查服务后重新连接" });
                    return;
                }
                if (Status.State != "reconnecting") logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "OneBot 命令机器人连接中断，正在重连");
                SetStatus(Status with { State = "reconnecting", Error = "OneBot 连接中断，正在重连，请检查服务、地址和网络" });
                var seconds = Math.Min(Math.Pow(2, Math.Min(attempts++, 6)), 60);
                try { await Task.Delay(TimeSpan.FromSeconds(seconds * (1 + Random.Shared.NextDouble() * 0.2)), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
    }
    internal static bool IsAllowed(NotificationSettings settings, string? type, CommandBotMessage message)
    {
        var user = (settings.OneBotUserId ?? "").Trim();
        var group = (settings.OneBotGroupId ?? "").Trim();
        return type switch
        {
            "private" => user.Length > 0 && message.Sender == user,
            "group" => group.Length > 0 && message.ChatId == group && (user.Length == 0 || message.Sender == user),
            _ => false
        };
    }
}

internal delegate Task OneBotConnectAsync(OneBotCommandBotOptions options, Action connected, Action<JsonElement> receive, CancellationToken token);

internal sealed class OneBotConnectionException(string message, bool permanent) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}
