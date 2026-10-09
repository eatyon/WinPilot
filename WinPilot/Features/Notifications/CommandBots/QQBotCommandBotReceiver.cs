using System.Net;
using System.Text.Json;
using WinPilot.Core;

namespace WinPilot.Features.Notifications.CommandBots;

internal sealed class QQBotCommandBotReceiver(ILogger<QQBotCommandBotService> logger, QQBotConnectAsync connect,
    Action<JsonElement> rawReceive, Func<CommandBotStatus> status, Action<CommandBotStatus> publish, Action<CommandBotMessage> receive)
    : CommandBotReceiver(status, publish, receive)
{
    public override async Task RunAsync(WinPilotSettings configuration, CancellationToken token)
    {
        var settings = configuration.Notifications ?? new();
        var session = new QQBotGatewaySession();
        var seen = new Dictionary<string, DateTimeOffset>();
        var attempts = 0;
        var authenticationFailures = 0;
        while (!token.IsCancellationRequested)
        {
            long? connectedAt = null;
            try
            {
                await connect(settings, session, () =>
                {
                    connectedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    authenticationFailures = 0;
                    SetStatus(Status with { State = "connected", Error = null });
                }, payload =>
                {
                    if (token.IsCancellationRequested) return;
                    rawReceive(payload);
                    var message = QQBotCommandBotTransport.ParseMessage(payload, session.BotId);
                    var id = payload.TryGetProperty("d", out var data) ? QQBotGatewayClient.StringAt(data, "id") : null;
                    if (message is null || string.IsNullOrWhiteSpace(id)) return;
                    if (!IsAllowed(settings, QQBotGatewayClient.StringAt(payload, "t"), message)) return;
                    var now = DateTimeOffset.UtcNow;
                    foreach (var key in seen.Where(entry => now - entry.Value > TimeSpan.FromMinutes(5)).Select(entry => entry.Key).ToArray()) seen.Remove(key);
                    if (!seen.TryAdd(id, now)) return;
                    if (seen.Count > 4096) seen.Remove(seen.MinBy(entry => entry.Value).Key);
                    Receive(message);
                }, token);
                if (!token.IsCancellationRequested) throw new IOException();
            }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            catch (QQBotConnectionException exception) when (exception.Permanent)
            {
                SetStatus(Status with { State = "failed", Error = exception.Message });
                return;
            }
            catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                SetStatus(Status with { State = "failed", Error = "QQBot 凭据或权限无效，请检查 AppID、AppSecret 和平台权限后重新连接" });
                return;
            }
            catch (InvalidOperationException)
            {
                SetStatus(Status with { State = "failed", Error = "QQBot 返回的访问令牌或网关信息无效，请检查平台配置后重新连接" });
                return;
            }
            catch (Exception exception)
            {
                if (exception is QQBotGatewayClosedException { Code: 4004 })
                {
                    session.Reset();
                    if (++authenticationFailures >= 2)
                    {
                        SetStatus(Status with { State = "failed", Error = "QQBot 认证失败，请检查 AppID、AppSecret 后重新连接" });
                        return;
                    }
                }
                else authenticationFailures = 0;
                if (connectedAt is { } timestamp && System.Diagnostics.Stopwatch.GetElapsedTime(timestamp) >= TimeSpan.FromMinutes(2)) attempts = 0;
                if (attempts >= 100)
                {
                    SetStatus(Status with { State = "failed", Error = "QQBot 自动重连次数已达上限，请检查网络后重新连接" });
                    return;
                }
                var seconds = exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } or QQBotGatewayClosedException { Code: 4008 }
                    ? 60 : Math.Min(Math.Pow(2, Math.Min(attempts, 6)), 60);
                attempts++;
                if (Status.State != "reconnecting") logger.LogWarning("QQBot 命令机器人连接中断，正在重连");
                SetStatus(Status with { State = "reconnecting", Error = "无法连接 QQBot，请检查网络和平台配置，正在重连" });
                try { await Task.Delay(TimeSpan.FromSeconds(seconds * (1 + Random.Shared.NextDouble() * 0.2)), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
    }
    internal static bool IsAllowed(NotificationSettings settings, string? type, CommandBotMessage message)
    {
        var user = (settings.QQBotOpenId ?? "").Trim();
        var group = (settings.QQBotGroupOpenId ?? "").Trim();
        return type switch
        {
            "C2C_MESSAGE_CREATE" => user.Length > 0 && message.Sender == user,
            "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE" => group.Length > 0 && message.ChatId == group && (user.Length == 0 || message.Sender == user),
            _ => false
        };
    }
}

internal delegate Task QQBotConnectAsync(NotificationSettings settings, QQBotGatewaySession session,
    Action connected, Action<JsonElement> receive, CancellationToken token);

internal sealed class QQBotConnectionException(string message, bool permanent) : Exception(message)
{
    public bool Permanent { get; } = permanent;
}
