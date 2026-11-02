using System.Net.WebSockets;
using System.Text.Json;
using WinPilot.Core;
using static WinPilot.Features.Notifications.QQBotGatewayClient;

namespace WinPilot.Features.Notifications.CommandBots;

internal sealed class QQBotGatewaySession
{
    public string? Id { get; set; }
    public long? Sequence { get; set; }
    public string? BotId { get; set; }
    public void Reset() { Id = null; Sequence = null; BotId = null; }
}

internal static class QQBotCommandBotTransport
{
    internal static async Task RunAsync(NotificationSettings settings, QQBotGatewaySession resume, Action connected,
        Action<JsonElement> receive, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var access = await GetAccessTokenAsync(client, settings.QQBotAppId, settings.QQBotClientSecret, token);
        var gateway = await GetGatewayAsync(client, access, token);
        if (!Uri.TryCreate(gateway, UriKind.Absolute, out var uri) || uri.Scheme != "wss")
            throw new QQBotConnectionException("QQBot 网关地址无效", true);
        using var socket = new ClientWebSocket();
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(uri, handshake.Token);
        await RunSessionAsync(socket, access, resume, connected, receive, token);
    }

    internal static async Task RunSessionAsync(WebSocket socket, string access, QQBotGatewaySession resume,
        Action connected, Action<JsonElement> receive, CancellationToken token)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        session.CancelAfter(TimeSpan.FromSeconds(30));
        Task? heartbeat = null;
        long sequence = resume.Sequence ?? 0;
        var awaitingAck = 0;
        var interval = 0;
        try
        {
            var hello = await ReceiveAsync(socket, session.Token);
            if (hello.GetProperty("op").GetInt32() != 10)
                throw new InvalidDataException("QQBot 网关握手无效");
            interval = hello.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
            if (interval <= 0 || interval > 600000) throw new InvalidDataException("QQBot 心跳间隔无效");
            var identify = resume.Id is not null && resume.Sequence.HasValue
                ? JsonSerializer.Serialize(new { op = 6, d = new { token = $"QQBot {access}", session_id = resume.Id, seq = resume.Sequence } })
                : JsonSerializer.Serialize(new { op = 2, d = new { token = $"QQBot {access}", intents = Intents, shard = new[] { 0, 1 } } });
            await SendAsync(socket, identify, session.Token);
            heartbeat = HeartbeatAsync();
            while (true)
            {
                var payload = await ReceiveAsync(socket, session.Token);
                var op = payload.GetProperty("op").GetInt32();
                if (payload.TryGetProperty("s", out var seq) && seq.ValueKind == JsonValueKind.Number && seq.TryGetInt64(out var value))
                {
                    Interlocked.Exchange(ref sequence, value);
                    resume.Sequence = value;
                }
                if (op == 11) { Interlocked.Exchange(ref awaitingAck, 0); continue; }
                if (op == 7) throw new IOException("QQBot 请求重连");
                if (op == 9)
                {
                    if (!payload.TryGetProperty("d", out var resumable) || resumable.ValueKind != JsonValueKind.True) resume.Reset();
                    throw new IOException("QQBot 会话需要重新连接");
                }
                if (op != 0) continue;
                var type = StringAt(payload, "t");
                if (type is "READY" or "RESUMED")
                {
                    if (type == "READY")
                    {
                        resume.Id = StringAt(payload.GetProperty("d"), "session_id") ?? throw new InvalidDataException("QQBot 会话标识无效");
                        if (payload.GetProperty("d").TryGetProperty("user", out var botUser)) resume.BotId = StringAt(botUser, "id");
                    }
                    session.CancelAfter(Timeout.InfiniteTimeSpan);
                    connected();
                }
                receive(payload);
            }
        }
        catch (QQBotGatewayClosedException exception)
        {
            if (exception.Code is 4006 or 4007 or 4009) resume.Reset();
            if (exception.Code is 4914 or 4915)
                throw new QQBotConnectionException("QQBot 凭据或事件权限无效，请检查平台配置后重新连接", true);
            throw;
        }
        finally
        {
            session.Cancel();
            socket.Abort();
            if (heartbeat is not null)
                try { await heartbeat; } catch { }
        }

        async Task HeartbeatAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(interval, session.Token);
                    if (Interlocked.Exchange(ref awaitingAck, 1) != 0)
                        throw new IOException("QQBot 心跳响应超时");
                    using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    sendTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var current = Interlocked.Read(ref sequence);
                    await SendAsync(socket, JsonSerializer.Serialize(new { op = 1, d = current == 0 ? (long?)null : current }), sendTimeout.Token);
                }
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            catch
            {
                session.Cancel();
                socket.Abort();
                throw;
            }
        }
    }

    internal static CommandBotMessage? ParseMessage(JsonElement payload, string? botId = null)
    {
        if (!payload.TryGetProperty("op", out var op) || !op.TryGetInt32(out var code) || code != 0 ||
            !payload.TryGetProperty("d", out var data)) return null;
        var type = StringAt(payload, "t");
        if (type is not ("C2C_MESSAGE_CREATE" or "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE")) return null;
        if (!data.TryGetProperty("author", out var author)) return null;
        if (author.TryGetProperty("bot", out var isBot) && isBot.ValueKind == JsonValueKind.True) return null;
        var sender = StringAt(author, type == "C2C_MESSAGE_CREATE" ? "user_openid" : "member_openid");
        var chat = type == "C2C_MESSAGE_CREATE" ? sender : StringAt(data, "group_openid");
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(chat)) return null;
        var text = StringAt(data, "content")?.Trim();
        if (string.IsNullOrEmpty(text)) return new(chat, sender, "[非文字消息]") { CommandText = "" };
        var commandText = text;
        if (type != "C2C_MESSAGE_CREATE" && !string.IsNullOrEmpty(botId))
        {
            commandText = CommandBotService.StripLeadingBotMention(commandText, $"<@!{botId}>", false);
            commandText = CommandBotService.StripLeadingBotMention(commandText, $"<@{botId}>", false);
        }
        return new(chat, sender, text)
        {
            CommandText = commandText,
            ConversationType = type == "C2C_MESSAGE_CREATE" ? "private" : "group",
            MessageId = StringAt(data, "id")
        };
    }
}
