using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using WinPilot.Features.Notifications.CommandBots;

namespace WinPilot.Features.Notifications;

internal sealed class OneBotWebSocketSender(WebSocket socket)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> pending = new();
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly object lifecycle = new();
    private int activeSends;
    private int closed;

    internal async Task SendAsync(CommandBotMessage message, string text, byte[]? image, CancellationToken token)
    {
        if (message.ConversationType is not ("private" or "group")) throw new InvalidOperationException("OneBot 回复上下文不完整");
        var echo = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifecycle)
        {
            if (closed != 0) throw new IOException("OneBot 回复连接已关闭");
            activeSends++;
        }
        pending[echo] = completion;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var parameters = new Dictionary<string, object?>
            {
                ["message_type"] = message.ConversationType,
                [message.ConversationType == "private" ? "user_id" : "group_id"] = message.ChatId,
                ["message"] = OneBotNotificationChannel.CreateSegments(text, image)
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { action = "send_msg", @params = parameters, echo });
            await sending.WaitAsync(timeout.Token);
            try
            {
                if (Volatile.Read(ref closed) != 0 || socket.State != WebSocketState.Open) throw new IOException("OneBot 回复连接已关闭");
                await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, timeout.Token);
            }
            finally { sending.Release(); }
            if (!await completion.Task.WaitAsync(timeout.Token)) throw new InvalidOperationException("OneBot 回复失败或连接已关闭");
        }
        finally
        {
            pending.TryRemove(echo, out _);
            lock (lifecycle)
                if (--activeSends == 0 && closed != 0) sending.Dispose();
        }
    }

    internal bool HandleResponse(JsonElement payload)
    {
        if (!payload.TryGetProperty("echo", out var echo) || echo.ValueKind != JsonValueKind.String ||
            !pending.TryGetValue(echo.GetString()!, out var completion)) return false;
        if (payload.TryGetProperty("status", out var status) && status.GetString() == "ok" &&
            payload.TryGetProperty("retcode", out var code) && code.TryGetInt32(out var value) && value == 0)
            completion.TrySetResult(true);
        else completion.TrySetResult(false);
        return true;
    }

    internal void Close()
    {
        lock (lifecycle)
        {
            if (closed != 0) return;
            Volatile.Write(ref closed, 1);
            foreach (var completion in pending.Values) completion.TrySetResult(false);
            if (activeSends == 0) sending.Dispose();
        }
    }
}
