using System.Net.WebSockets;
using System.Text.Json;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;

namespace WinPilot.Web;

public static class TerminalEndpointRouteBuilderExtensions
{
    private const int MaximumMessageBytes = 16 * 1024;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map("/api/terminal/ws", HandleAsync);
        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context, TerminalSessionManager manager, SafeModeService safeMode, ILogger<TerminalSessionManager> logger)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (safeMode.Enabled) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, safeMode.RestrictedOperationsCancellation);
        var cancellationToken = connectionCancellation.Token;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connectionId = Guid.NewGuid();
        using var sendLock = new SemaphoreSlim(1, 1);

        async Task Publish(TerminalEvent item)
        {
            if (socket.State != WebSocketState.Open || cancellationToken.IsCancellationRequested) return;
            using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendCancellation.CancelAfter(SendTimeout);
            var acquired = false;
            try
            {
                await sendLock.WaitAsync(sendCancellation.Token);
                acquired = true;
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions), WebSocketMessageType.Text, true, sendCancellation.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
                connectionCancellation.Cancel();
                socket.Abort();
            }
            finally { if (acquired) sendLock.Release(); }
        }

        try
        {
            var buffer = new byte[MaximumMessageBytes];
            using var message = new MemoryStream();
            var invalidMessageType = false;
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (message.Length + result.Count > MaximumMessageBytes)
                {
                    using var closeCancellation = new CancellationTokenSource(SendTimeout);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "终端消息过大", closeCancellation.Token);
                    break;
                }
                invalidMessageType |= result.MessageType != WebSocketMessageType.Text;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                TerminalCommand? command;
                try
                {
                    if (invalidMessageType) throw new JsonException();
                    command = JsonSerializer.Deserialize<TerminalCommand>(message.GetBuffer().AsSpan(0, (int)message.Length), JsonOptions);
                }
                catch (JsonException)
                {
                    await Publish(new TerminalEvent("error", null, null, "终端消息格式无效"));
                    continue;
                }
                finally
                {
                    message.SetLength(0);
                    invalidMessageType = false;
                }

                try
                {
                    if (command?.Type is "input" or "interrupt" or "resize" or "close" && command.SessionId is null)
                    {
                        await Publish(new TerminalEvent("error", null, null, "缺少终端会话 ID"));
                        continue;
                    }
                    switch (command?.Type)
                    {
                        case "create":
                            {
                                var info = await manager.CreateAsync(connectionId, command.Shell ?? "powershell", command.WorkingDirectory, Publish, cancellationToken);
                                cancellationToken.ThrowIfCancellationRequested();
                                await Publish(new TerminalEvent("created", info.SessionId, null, JsonSerializer.Serialize(info, JsonOptions)));
                                break;
                            }
                        case "input": await manager.InputAsync(connectionId, command.SessionId!.Value, command.Data ?? string.Empty); break;
                        case "interrupt": await manager.InterruptAsync(connectionId, command.SessionId!.Value); break;
                        case "resize": await manager.ResizeAsync(connectionId, command.SessionId!.Value, command.Columns ?? 0, command.Rows ?? 0); break;
                        case "close": await manager.CloseAsync(connectionId, command.SessionId!.Value); break;
                        default: await Publish(new TerminalEvent("error", command?.SessionId, null, "不支持的终端操作")); break;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or DirectoryNotFoundException or FileNotFoundException or System.ComponentModel.Win32Exception)
                {
                    logger.LogDebug(exception, "终端操作失败，操作：{Operation}，会话 ID：{TerminalSessionId}", command?.Type, command?.SessionId);
                    await Publish(new TerminalEvent("error", command?.SessionId, null, exception.Message));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException exception) { logger.LogDebug(exception, "终端连接已断开"); }
        finally
        {
            connectionCancellation.Cancel();
            await manager.CloseConnectionAsync(connectionId);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "连接已关闭", closeCancellation.Token); }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { socket.Abort(); }
            }
        }
    }

    private sealed record TerminalCommand(string Type, Guid? SessionId, string? Shell, string? WorkingDirectory, string? Data, int? Columns, int? Rows);
}
