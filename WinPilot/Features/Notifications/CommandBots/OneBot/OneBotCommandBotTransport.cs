using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinPilot.Features.Notifications.CommandBots;

internal static partial class OneBotCommandBotTransport
{
    private const int MaxMessageBytes = 1024 * 1024;
    private const int MaxCommandCharacters = 8192;

    internal static async Task RunAsync(OneBotCommandBotOptions options, Action connected, Action<JsonElement> receive, CancellationToken token,
        Action<Func<CommandBotMessage, string, byte[]?, CancellationToken, Task>?>? publishReply = null)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrWhiteSpace(options.Token)) socket.Options.SetRequestHeader("Authorization", $"Bearer {options.Token}");
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(15));
        try { await socket.ConnectAsync(new Uri(options.WebSocketUrl), handshake.Token); }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { throw new OneBotConnectionException("OneBot 拒绝认证，请检查 WebSocket Token 后重新连接", true); }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        { throw new OneBotConnectionException("OneBot WebSocket 接口无效，请检查完整连接地址后重新连接", true); }
        var sender = new OneBotWebSocketSender(socket);
        var eventOnly = new Uri(options.WebSocketUrl).AbsolutePath.TrimEnd('/').EndsWith("/event", StringComparison.OrdinalIgnoreCase);
        publishReply?.Invoke(eventOnly
            ? (message, text, image, cancellation) => SendViaApiAsync(options, message, text, image, cancellation)
            : sender.SendAsync);
        try
        {
            connected();
            await RunSessionAsync(socket, receive, token, sender);
        }
        finally { publishReply?.Invoke(null); sender.Close(); }
    }

    private static async Task SendViaApiAsync(OneBotCommandBotOptions options, CommandBotMessage message,
        string text, byte[]? image, CancellationToken token)
    {
        var uri = new UriBuilder(options.WebSocketUrl);
        uri.Path = uri.Path.TrimEnd('/')[..^5] + "api";
        using var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(options.Token)) socket.Options.SetRequestHeader("Authorization", $"Bearer {options.Token}");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(uri.Uri, lifetime.Token);
        var sender = new OneBotWebSocketSender(socket);
        var reading = RunSessionAsync(socket, _ => { }, lifetime.Token, sender);
        try { await sender.SendAsync(message, text, image, lifetime.Token); }
        finally
        {
            lifetime.Cancel();
            socket.Abort();
            sender.Close();
            try { await reading; }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        }
    }

    internal static async Task RunSessionAsync(WebSocket socket, Action<JsonElement> receive, CancellationToken token,
        OneBotWebSocketSender? sender = null)
    {
        TimeSpan? heartbeatTimeout = null;
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                var wait = heartbeatTimeout;
                if (wait.HasValue) timeout.CancelAfter(wait.Value);
                using var data = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (result.CloseStatus is WebSocketCloseStatus.PolicyViolation or WebSocketCloseStatus.InvalidMessageType)
                            throw new OneBotConnectionException("OneBot 拒绝连接，请检查 Token 和事件接口后重新连接", true);
                        throw new IOException("OneBot 连接已关闭");
                    }
                    if (result.MessageType != WebSocketMessageType.Text || data.Length + result.Count > MaxMessageBytes)
                        throw new InvalidDataException("OneBot 消息类型或大小无效");
                    data.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                var payload = document.RootElement;
                if (sender?.HandleResponse(payload) == true) continue;
                var receptionEvent = IsReceptionEvent(payload);
                if (receptionEvent && StringAt(payload, "meta_event_type") == "heartbeat" && payload.TryGetProperty("interval", out var interval) &&
                    interval.ValueKind == JsonValueKind.Number && interval.TryGetInt64(out var milliseconds) && milliseconds > 0)
                    heartbeatTimeout = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 1000, 3600000) * 2 + 5000);
                receive(payload);
            }
        }
        finally { socket.Abort(); sender?.Close(); }
    }

    internal static bool IsReceptionEvent(JsonElement payload)
    {
        if (ScalarAt(payload, "self_id") is null) return false;
        var type = StringAt(payload, "post_type");
        return type == "message" && StringAt(payload, "message_type") is "private" or "group" ||
            type == "meta_event" && StringAt(payload, "meta_event_type") is "lifecycle" or "heartbeat";
    }

    internal static (string Key, CommandBotMessage Message)? ParseMessage(JsonElement payload)
    {
        if (StringAt(payload, "post_type") != "message") return null;
        var type = StringAt(payload, "message_type");
        if (type is not ("private" or "group")) return null;
        var self = ScalarAt(payload, "self_id");
        var sender = ScalarAt(payload, "user_id");
        var chat = type == "private" ? sender : ScalarAt(payload, "group_id");
        var id = ScalarAt(payload, "message_id");
        if (self is null || sender is null || chat is null || id is null || sender == self) return null;
        var content = ExtractText(payload, type == "group" ? self : null);
        return ($"{self}:{type}:{chat}:{id}", new(chat, sender, content)
        {
            ConversationType = type, MessageId = id
        });
    }

    private static string ExtractText(JsonElement payload, string? botId)
    {
        var builder = new StringBuilder();
        var hasText = false;
        if (payload.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in message.EnumerateArray())
            {
                var type = StringAt(segment, "type");
                if (type == "text" && segment.TryGetProperty("data", out var data))
                {
                    var text = StringAt(data, "text");
                    hasText |= !string.IsNullOrWhiteSpace(text);
                    builder.Append(text);
                }
                else if (type == "at" && segment.TryGetProperty("data", out var mention))
                {
                    var mentioned = ScalarAt(mention, "qq");
                    if (botId is not null && mentioned == botId && string.IsNullOrWhiteSpace(builder.ToString())) continue;
                    builder.Append('@').Append(mentioned ?? "未知");
                }
                if (builder.Length > MaxCommandCharacters) break;
            }
        }
        else
        {
            var raw = message.ValueKind == JsonValueKind.String ? message.GetString() : StringAt(payload, "raw_message");
            hasText = !string.IsNullOrWhiteSpace(CqSegment().Replace(raw ?? "", ""));
            if (botId is not null)
                raw = CommandBotService.StripLeadingBotMention(raw ?? "", $"[CQ:at,qq={botId}]", false);
            builder.Append(Decode(CqSegment().Replace(raw ?? "", match =>
                match.Groups[1].Value != "at" ? "" : FormatCqMention(match))));
        }
        if (!hasText) return "";
        if (builder.Length > MaxCommandCharacters) { builder.Length = MaxCommandCharacters; builder.Append('…'); }
        return builder.ToString();
    }

    private static string Decode(string text) => text.Replace("&#91;", "[").Replace("&#93;", "]").Replace("&#44;", ",").Replace("&amp;", "&");
    private static string FormatCqMention(Match match)
    {
        var qq = match.Groups[2].Value.Split(',').FirstOrDefault(value => value.StartsWith("qq=", StringComparison.Ordinal))?[3..];
        return qq == "all" ? "@全体成员" : qq is { Length: > 0 } && qq.All(char.IsAsciiDigit) ? $"@{qq}" : "[@]";
    }
    private static string? StringAt(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static string? ScalarAt(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var item)) return null;
        var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ValueKind == JsonValueKind.Number ? item.GetRawText() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
    [GeneratedRegex(@"\[CQ:([A-Za-z0-9_]+)(?:,([^\]]*))?\]", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CqSegment();
}
