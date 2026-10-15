using System.Net.WebSockets;
using System.Text.Json;
using static WinPilot.Features.Notifications.QQBotGatewayClient;

namespace WinPilot.Features.Notifications;

public static class QQBotBindingService
{
    public static async Task<string> BindAsync(
        string appId,
        string secret,
        bool group,
        Func<string, CancellationToken, Task> reportCode,
        CancellationToken requestToken)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
            throw new QQBotBindingValidationException("QQBot AppID 或 AppSecret 未配置");
        return await WithTimeoutAsync(async token =>
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var (access, gateway) = await GetConnectionAsync(client, appId, secret, token);
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(gateway), token);
            return await BindSessionAsync(socket, access, group, reportCode, token);
        }, requestToken, TimeSpan.FromSeconds(60));
    }

    internal static async Task<(string Access, string Gateway)> GetConnectionAsync(HttpClient client, string appId, string secret, CancellationToken token)
    {
        try
        {
            var access = await GetAccessTokenAsync(client, appId, secret, token);
            return (access, await GetGatewayAsync(client, access, token));
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested && exception.InnerException is TimeoutException)
        { throw new HttpRequestException("QQBot HTTP 请求超时", exception); }
    }

    internal static async Task<string> WithTimeoutAsync(Func<CancellationToken, Task<string>> action, CancellationToken token, TimeSpan duration)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(duration);
        try { return await action(timeout.Token); }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new QQBotBindingTimeoutException(exception); }
    }

    internal static async Task<string> BindSessionAsync(
        WebSocket socket,
        string access,
        bool group,
        Func<string, CancellationToken, Task> reportCode,
        CancellationToken requestToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        Task? heartbeat = null;
        var sequence = 0L;
        try
        {
            var hello = await ReceiveAsync(socket, session.Token);
            var interval = hello.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
            if (interval <= 0) throw new InvalidOperationException("QQBot 网关心跳间隔无效");
            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                op = 2,
                d = new { token = $"QQBot {access}", intents = Intents, shard = new[] { 0, 1 } }
            }), session.Token);
            var code = Random.Shared.Next(1000, 10000).ToString();
            await reportCode(code, session.Token);
            heartbeat = HeartbeatAsync(socket, interval, () => Interlocked.Read(ref sequence), session);
            while (true)
            {
                var payload = await ReceiveAsync(socket, session.Token);
                if (payload.TryGetProperty("s", out var seq) && seq.ValueKind == JsonValueKind.Number)
                    Interlocked.Exchange(ref sequence, seq.GetInt64());
                var id = FindBoundId(payload, group, code);
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
        }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested && session.IsCancellationRequested && heartbeat is not null)
        {
            await heartbeat;
            throw;
        }
        finally
        {
            session.Cancel();
            if (heartbeat is not null)
            {
                try { await heartbeat; }
                catch { }
            }
            await CloseAsync(socket);
        }
    }

    internal static string? FindBoundId(JsonElement payload, bool group, string code)
    {
        if (payload.GetProperty("op").GetInt32() != 0 || !payload.TryGetProperty("d", out var data)) return null;
        var type = StringAt(payload, "t");
        if (!group && type == "FRIEND_ADD") return StringAt(data, "openid");
        if (group && type == "GROUP_ADD_ROBOT") return StringAt(data, "group_openid");
        if (StringAt(data, "content")?.Contains(code) != true) return null;
        if (!group && type == "C2C_MESSAGE_CREATE")
            return data.TryGetProperty("author", out var author) ? StringAt(author, "user_openid") : null;
        if (group && type is "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE")
            return StringAt(data, "group_openid");
        return null;
    }

    private static async Task HeartbeatAsync(WebSocket socket, int interval, Func<long> sequence, CancellationTokenSource session)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, session.Token);
                var currentSequence = sequence();
                await SendAsync(socket, JsonSerializer.Serialize(new
                {
                    op = 1,
                    d = currentSequence == 0 ? (long?)null : currentSequence
                }), session.Token);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        catch
        {
            session.Cancel();
            throw;
        }
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "binding complete", timeout.Token); }
        catch { socket.Abort(); }
    }

}
