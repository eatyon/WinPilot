using System.Buffers;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WinPilot.Features.Notifications;

internal static class QQBotGatewayClient
{
    private const string TokenUrl = "https://bots.qq.com/app/getAppAccessToken";
    private const string GatewayUrl = "https://api.sgroup.qq.com/gateway";
    internal const int Intents = 1 << 25;

    internal static async Task<string> GetAccessTokenAsync(HttpClient client, string appId, string secret, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(TokenUrl, new { appId, clientSecret = secret }, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return StringAt(document.RootElement, "access_token") ?? throw new InvalidOperationException("QQBot 访问令牌无效");
    }

    internal static async Task<string> GetGatewayAsync(HttpClient client, string access, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GatewayUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", access);
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return StringAt(document.RootElement, "url") ?? throw new InvalidOperationException("QQBot 网关地址无效");
    }

    internal static string? StringAt(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    internal static async Task SendAsync(WebSocket socket, string text, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }

    internal static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var data = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, 8192), token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new QQBotGatewayClosedException((int?)result.CloseStatus);
                if (data.Length + result.Count > 1024 * 1024)
                    throw new InvalidDataException("QQBot 网关消息超过大小限制");
                data.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            using var document = JsonDocument.Parse(data.GetBuffer().AsMemory(0, (int)data.Length));
            return document.RootElement.Clone();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

internal sealed class QQBotGatewayClosedException(int? code) : IOException("QQBot 网关已关闭连接")
{
    public int? Code { get; } = code;
}
