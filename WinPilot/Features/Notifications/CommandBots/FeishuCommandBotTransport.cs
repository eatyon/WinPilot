using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using ProtoBuf;
using WinPilot.Core;

namespace WinPilot.Features.Notifications.CommandBots;

internal static class FeishuCommandBotTransport
{
    private const int MaxMessageBytes = 1024 * 1024;

    internal static async Task RunAsync(NotificationSettings settings, Action connected, Action<CommandBotMessage> receive,
        Dictionary<string, DateTimeOffset> seen, Action<FeishuClientConfig> configure, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var (endpoint, service, config) = await GetEndpointAsync(settings, client, token);
        string? botOpenId = null;
        try { botOpenId = await GetBotOpenIdAsync(settings, client, token); }
        catch (Exception exception) when (!token.IsCancellationRequested &&
            exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
        {
        }
        configure(config);
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await socket.ConnectAsync(endpoint, timeout.Token); }
        catch (WebSocketException) when (socket.HttpStatusCode == System.Net.HttpStatusCode.Forbidden ||
            socket.HttpResponseHeaders?.TryGetValue("Handshake-Autherrcode", out var errors) == true && errors.Contains("1000040350"))
        { throw new FeishuConnectionException("飞书拒绝连接，请检查应用权限或关闭其他接收客户端", true); }
        connected();
        await RunSessionAsync(socket, service, config, receive, seen, configure, token, botOpenId);
    }

    private static async Task<string?> GetBotOpenIdAsync(NotificationSettings settings, HttpClient client, CancellationToken token)
    {
        using var auth = await client.PostAsJsonAsync("https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal",
            new { app_id = settings.FeishuAppId, app_secret = settings.FeishuAppSecret }, token);
        auth.EnsureSuccessStatusCode();
        using var credentials = JsonDocument.Parse(await auth.Content.ReadAsStringAsync(token));
        var access = credentials.RootElement.GetProperty("tenant_access_token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://open.feishu.cn/open-apis/bot/v3/info");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return info.RootElement.TryGetProperty("bot", out var bot) && bot.TryGetProperty("open_id", out var id) ? id.GetString() : null;
    }

    internal static async Task<(Uri Endpoint, int Service, FeishuClientConfig Config)> GetEndpointAsync(
        NotificationSettings settings, HttpClient client, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("https://open.feishu.cn/callback/ws/endpoint",
            new { AppID = settings.FeishuAppId.Trim(), AppSecret = settings.FeishuAppSecret.Trim() }, new JsonSerializerOptions(), token);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = document.RootElement;
        var code = root.GetProperty("code").GetInt32();
        if (!response.IsSuccessStatusCode || code != 0)
            throw EndpointError(code, (int)response.StatusCode);
        var data = root.GetProperty("data");
        var endpoint = new Uri(data.GetProperty("URL").GetString()!);
        if (endpoint.Scheme != "wss") throw new FeishuConnectionException("飞书返回的连接地址无效", true);
        var service = int.Parse(QueryHelpers.ParseQuery(endpoint.Query)["service_id"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
        var config = ReadConfig(data.TryGetProperty("ClientConfig", out var value) ? value : default, new());
        return (endpoint, service, config);
    }

    internal static FeishuConnectionException EndpointError(int code, int httpStatus) => new(code switch
    {
        403 or 514 => "飞书应用凭据无效或无权连接，请检查 AppId、AppSecret 和应用配置",
        1000040350 => "飞书应用连接数量已达上限，请关闭其他接收客户端",
        _ => $"飞书长连接初始化失败（{code}），请检查应用凭据和长连接配置"
    }, code is not (0 or 1 or 1000040343) || httpStatus is 400 or 401 or 403 or 404);

    internal static async Task RunSessionAsync(WebSocket socket, int service, FeishuClientConfig config,
        Action<CommandBotMessage> receive, Dictionary<string, DateTimeOffset> seen,
        Action<FeishuClientConfig> configure, CancellationToken token, string? botOpenId = null)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var writes = new SemaphoreSlim(1, 1);
        var fragments = new Dictionary<string, FragmentSet>();
        var lastPong = Environment.TickCount64;
        var pingInterval = config.PingInterval;

        async Task SendAsync(FeishuFrame frame)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await writes.WaitAsync(timeout.Token);
            try
            {
                using var buffer = new MemoryStream();
                Serializer.Serialize(buffer, frame);
                await socket.SendAsync(buffer.ToArray().AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            }
            finally { writes.Release(); }
        }
        async Task PingAsync()
        {
            try
            {
                while (!session.IsCancellationRequested)
                {
                    if (Environment.TickCount64 - Interlocked.Read(ref lastPong) > (Volatile.Read(ref pingInterval) * 2L + 5) * 1000)
                        throw new IOException("飞书心跳超时");
                    await SendAsync(new() { Service = service, Headers = [new() { Key = "type", Value = "ping" }] });
                    await Task.Delay(TimeSpan.FromSeconds(Volatile.Read(ref pingInterval)), session.Token);
                }
            }
            catch { session.Cancel(); socket.Abort(); throw; }
        }

        var heartbeat = PingAsync();
        try
        {
            var buffer = new byte[8192];
            while (!session.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), session.Token);
                    if (result.MessageType == WebSocketMessageType.Close) throw new IOException("飞书连接已关闭");
                    if (message.Length + result.Count > MaxMessageBytes) throw new InvalidDataException("飞书消息过大");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Binary) continue;
                message.Position = 0;
                var frame = Serializer.Deserialize<FeishuFrame>(message);
                if (frame.Method == 0 && frame.Header("type") == "pong")
                {
                    Interlocked.Exchange(ref lastPong, Environment.TickCount64);
                    if (frame.Payload.Length > 0)
                    {
                        using var pong = JsonDocument.Parse(frame.Payload);
                        config = ReadConfig(pong.RootElement, config);
                        Volatile.Write(ref pingInterval, config.PingInterval);
                        configure(config);
                    }
                    continue;
                }
                if (frame.Method != 1 || frame.Header("type") != "event") continue;
                var payload = Combine(frame, fragments);
                if (payload is null) continue;
                var success = true;
                try
                {
                    using var eventJson = JsonDocument.Parse(payload);
                    var received = ParseMessage(eventJson.RootElement, botOpenId);
                    if (received is not null)
                    {
                        var id = eventJson.RootElement.GetProperty("event").GetProperty("message").GetProperty("message_id").GetString()!;
                        var now = DateTimeOffset.UtcNow;
                        foreach (var key in seen.Where(pair => now - pair.Value > TimeSpan.FromMinutes(5)).Select(pair => pair.Key).ToArray()) seen.Remove(key);
                        if (!seen.ContainsKey(id))
                        {
                            if (seen.Count >= 4096) seen.Remove(seen.MinBy(pair => pair.Value).Key);
                            seen[id] = now;
                            receive(received);
                        }
                    }
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
                { success = false; }
                frame.Headers.Add(new() { Key = "biz_rt", Value = "0" });
                frame.Payload = JsonSerializer.SerializeToUtf8Bytes(new { code = success ? 200 : 500, headers = (object?)null, data = (object?)null });
                await SendAsync(frame);
            }
        }
        finally
        {
            session.Cancel();
            socket.Abort();
            try { await heartbeat; }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        }
    }

    internal static FeishuClientConfig ReadConfig(JsonElement config, FeishuClientConfig fallback)
    {
        if (config.ValueKind != JsonValueKind.Object) return fallback;
        int Read(string name, int previous, int min, int max) =>
            config.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number >= min
                ? Math.Clamp(number, min, max) : previous;
        return new(
            Read("PingInterval", fallback.PingInterval, 1, 600),
            Read("ReconnectCount", fallback.ReconnectCount, int.MinValue, int.MaxValue),
            Read("ReconnectInterval", fallback.ReconnectInterval, 1, 3600),
            Read("ReconnectNonce", fallback.ReconnectNonce, 0, 3600));
    }

    internal static CommandBotMessage? ParseMessage(JsonElement root, string? botOpenId = null)
    {
        if (!root.TryGetProperty("header", out var header) ||
            !header.TryGetProperty("event_type", out var type) || type.GetString() != "im.message.receive_v1") return null;
        var body = root.GetProperty("event");
        var sender = body.GetProperty("sender");
        if (sender.GetProperty("sender_type").GetString() is "bot" or "app") return null;
        var message = body.GetProperty("message");
        if (message.GetProperty("message_type").GetString() != "text") return null;
        using var content = JsonDocument.Parse(message.GetProperty("content").GetString()!);
        var text = content.RootElement.GetProperty("text").GetString() ?? "";
        var commandText = text;
        if (message.TryGetProperty("mentions", out var mentions))
            foreach (var mention in mentions.EnumerateArray())
                if (mention.TryGetProperty("key", out var key) && !string.IsNullOrEmpty(key.GetString()))
                {
                    if (!string.IsNullOrEmpty(botOpenId) && mention.TryGetProperty("id", out var identity) &&
                        identity.TryGetProperty("open_id", out var mentionId) && mentionId.GetString() == botOpenId)
                        commandText = CommandBotService.StripLeadingBotMention(commandText, key.GetString());
                    var displayName = "@" + (mention.TryGetProperty("name", out var name) ? name.GetString() : "用户");
                    text = text.Replace(key.GetString()!, displayName, StringComparison.Ordinal);
                    commandText = commandText.Replace(key.GetString()!, displayName, StringComparison.Ordinal);
                }
        var senderId = sender.GetProperty("sender_id");
        return new(message.GetProperty("chat_id").GetString() ?? "",
            senderId.TryGetProperty("open_id", out var openId) ? openId.GetString() ?? "未知发送者" : "未知发送者",
            text)
        {
            CommandText = commandText,
            MessageId = message.TryGetProperty("message_id", out var id) ? id.GetString() : null,
            ConversationType = message.TryGetProperty("chat_type", out var chatType) ? chatType.GetString() : null
        };
    }

    internal static byte[]? Combine(FeishuFrame frame, Dictionary<string, FragmentSet> fragments)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in fragments.Where(pair => now - pair.Value.CreatedAt > TimeSpan.FromSeconds(5)).Select(pair => pair.Key).ToArray()) fragments.Remove(key);
        if (!int.TryParse(frame.Header("sum"), out var sum) || sum <= 1) return frame.Payload;
        if (sum > 256 || !int.TryParse(frame.Header("seq"), out var seq) || seq < 0 || seq >= sum)
            throw new InvalidDataException("飞书消息分片无效");
        var id = frame.Header("message_id");
        if (string.IsNullOrEmpty(id)) throw new InvalidDataException("飞书消息分片缺少 ID");
        if (!fragments.TryGetValue(id, out var set))
        {
            if (fragments.Count >= 32) throw new InvalidDataException("飞书消息分片过多");
            fragments[id] = set = new(new byte[sum][], now);
        }
        if (set.Parts.Length != sum) throw new InvalidDataException("飞书消息分片数量不一致");
        set.Parts[seq] = frame.Payload;
        if (set.Parts.Sum(part => part?.Length ?? 0) > MaxMessageBytes) throw new InvalidDataException("飞书消息过大");
        if (set.Parts.Any(part => part is null)) return null;
        fragments.Remove(id);
        return set.Parts.SelectMany(part => part).ToArray();
    }

    internal sealed record FragmentSet(byte[][] Parts, DateTimeOffset CreatedAt);
}

internal sealed record FeishuClientConfig(int PingInterval = 120, int ReconnectCount = -1,
    int ReconnectInterval = 2, int ReconnectNonce = 1);

[ProtoContract]
internal sealed class FeishuFrame
{
    [ProtoMember(1, IsRequired = true)] public ulong SeqId { get; set; }
    [ProtoMember(2, IsRequired = true)] public ulong LogId { get; set; }
    [ProtoMember(3, IsRequired = true)] public int Service { get; set; }
    [ProtoMember(4, IsRequired = true)] public int Method { get; set; }
    [ProtoMember(5)] public List<FeishuFrameHeader> Headers { get; set; } = [];
    [ProtoMember(6)] public string PayloadEncoding { get; set; } = "";
    [ProtoMember(7)] public string PayloadType { get; set; } = "";
    [ProtoMember(8)] public byte[] Payload { get; set; } = [];
    [ProtoMember(9)] public string LogIdNew { get; set; } = "";
    public string? Header(string key) => Headers.FirstOrDefault(header => header.Key == key)?.Value;
}

[ProtoContract]
internal sealed class FeishuFrameHeader
{
    [ProtoMember(1, IsRequired = true)] public string Key { get; set; } = "";
    [ProtoMember(2, IsRequired = true)] public string Value { get; set; } = "";
}
