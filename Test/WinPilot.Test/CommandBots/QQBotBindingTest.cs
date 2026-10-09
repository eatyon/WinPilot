using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WinPilot.Features.Notifications;

namespace WinPilot.Test;

public sealed class QQBotBindingTest
{
    [Theory]
    [InlineData(32)]
    [InlineData(8193)]
    [InlineData(1024 * 1024)]
    public async Task GatewayReceivesFragmentsAndRetainsParsedContent(int messageBytes)
    {
        using var socket = new TestSocket();
        var text = new string('x', messageBytes - 2);
        socket.Enqueue(text);
        var received = await QQBotGatewayClient.ReceiveAsync(socket, CancellationToken.None);
        socket.Enqueue("next");
        Assert.Equal("next", (await QQBotGatewayClient.ReceiveAsync(socket, CancellationToken.None)).GetString());
        Assert.Equal(text, received.GetString());
    }

    [Fact]
    public async Task GatewayRejectsMessagesLargerThanLimit()
    {
        using var socket = new TestSocket();
        socket.Enqueue(new string('x', 1024 * 1024 - 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => QQBotGatewayClient.ReceiveAsync(socket, CancellationToken.None));
    }

    [Fact]
    public async Task GatewayCanReceiveAgainAfterInvalidJson()
    {
        using var socket = new TestSocket();
        socket.EnqueueRaw(Encoding.UTF8.GetBytes("{"));
        await Assert.ThrowsAnyAsync<JsonException>(() => QQBotGatewayClient.ReceiveAsync(socket, CancellationToken.None));
        socket.Enqueue("valid");
        Assert.Equal("valid", (await QQBotGatewayClient.ReceiveAsync(socket, CancellationToken.None)).GetString());
    }

    [Theory]
    [InlineData("C2C_MESSAGE_CREATE", false, "发送1234", "user")]
    [InlineData("C2C_MESSAGE_CREATE", false, "发送5678", null)]
    [InlineData("C2C_MESSAGE_CREATE", true, "发送1234", null)]
    [InlineData("GROUP_AT_MESSAGE_CREATE", true, "发送1234", "group")]
    [InlineData("GROUP_MESSAGE_CREATE", true, "发送1234", "group")]
    [InlineData("GROUP_AT_MESSAGE_CREATE", false, "发送1234", null)]
    [InlineData("FRIEND_ADD", false, "", "friend")]
    [InlineData("GROUP_ADD_ROBOT", true, "", "group")]
    public void MatchesExistingBindingEventsAndCode(string type, bool group, string content, string? expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            op = 0, t = type, d = new { content, openid = "friend", group_openid = "group", author = new { user_openid = "user" } }
        }));
        Assert.Equal(expected, QQBotBindingService.FindBoundId(document.RootElement, group, "1234"));
    }

    [Fact]
    public async Task SuccessReportsCodeAndClosesWithoutBackgroundHeartbeat()
    {
        using var socket = new TestSocket();
        socket.Enqueue(new { op = 10, d = new { heartbeat_interval = 10000 } });
        var result = await QQBotBindingService.BindSessionAsync(socket, "access", false, (code, _) =>
        {
            socket.Enqueue(new { op = 0, t = "C2C_MESSAGE_CREATE", d = new { content = code, author = new { user_openid = "bound-user" } } });
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("bound-user", result);
        Assert.True(socket.CloseCalled);
        Assert.Equal(0, socket.HeartbeatCount);
        using var identify = JsonDocument.Parse(Assert.Single(socket.Sent));
        Assert.Equal("QQBot access", identify.RootElement.GetProperty("d").GetProperty("token").GetString());
    }

    [Fact]
    public async Task CancellationStopsWaitingAndClosesSocket()
    {
        using var socket = new TestSocket();
        socket.Enqueue(new { op = 10, d = new { heartbeat_interval = 10000 } });
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = QQBotBindingService.BindSessionAsync(socket, "access", false, (_, _) =>
        { ready.SetResult(); return Task.CompletedTask; }, cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => binding.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(socket.CloseCalled);
        Assert.Equal(0, socket.HeartbeatCount);
    }

    [Fact]
    public async Task HeartbeatFailureCancelsPendingReceiveAndReportsFailure()
    {
        using var socket = new TestSocket { FailHeartbeat = true };
        socket.Enqueue(new { op = 10, d = new { heartbeat_interval = 1 } });
        await Assert.ThrowsAsync<WebSocketException>(() => QQBotBindingService.BindSessionAsync(socket, "access", false,
            (_, _) => Task.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(socket.CloseCalled);
        Assert.Equal(1, socket.HeartbeatCount);
    }

    [Fact]
    public async Task UnresponsiveCloseHandshakeIsAbortedWithoutLosingResult()
    {
        using var socket = new TestSocket { BlockClose = true };
        socket.Enqueue(new { op = 10, d = new { heartbeat_interval = 10000 } });
        socket.Enqueue(new { op = 0, t = "GROUP_ADD_ROBOT", d = new { group_openid = "bound-group" } });
        Assert.Equal("bound-group", await QQBotBindingService.BindSessionAsync(socket, "access", true,
            (_, _) => Task.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task FailedCodeDeliveryAlsoClosesSession()
    {
        using var socket = new TestSocket();
        socket.Enqueue(new { op = 10, d = new { heartbeat_interval = 10000 } });
        await Assert.ThrowsAsync<IOException>(() => QQBotBindingService.BindSessionAsync(socket, "access", false,
            (_, _) => throw new IOException("模拟网页断开"), CancellationToken.None));
        Assert.True(socket.CloseCalled);
        Assert.Empty(socket.Sent.Where(text => JsonDocument.Parse(text).RootElement.GetProperty("op").GetInt32() == 1));
    }

    private sealed class TestSocket : WebSocket
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        private byte[]? pending;
        private int pendingOffset;
        private WebSocketState state = WebSocketState.Open;
        public ConcurrentQueue<string> Sent { get; } = new();
        public bool FailHeartbeat { get; init; }
        public bool BlockClose { get; init; }
        public bool CloseCalled { get; private set; }
        public bool Aborted { get; private set; }
        public int HeartbeatCount { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        public void Enqueue(object value) => incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
        public void EnqueueRaw(byte[] value) => incoming.Writer.TryWrite(value);
        public override void Abort() { Aborted = true; state = WebSocketState.Aborted; }
        public override void Dispose() { state = WebSocketState.Closed; incoming.Writer.TryComplete(); }
        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseCalled = true;
            if (BlockClose) await Task.Delay(Timeout.Infinite, cancellationToken);
            state = WebSocketState.Closed;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending ??= await incoming.Reader.ReadAsync(cancellationToken);
            var count = Math.Min(buffer.Count, pending.Length - pendingOffset);
            pending.AsSpan(pendingOffset, count).CopyTo(buffer.AsSpan());
            pendingOffset += count;
            var complete = pendingOffset == pending.Length;
            if (complete) { pending = null; pendingOffset = 0; }
            return new(count, WebSocketMessageType.Text, complete);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var text = Encoding.UTF8.GetString(buffer.AsSpan());
            using var payload = JsonDocument.Parse(text);
            if (payload.RootElement.GetProperty("op").GetInt32() == 1)
            {
                HeartbeatCount++;
                if (FailHeartbeat) throw new WebSocketException("模拟心跳失败");
            }
            Sent.Enqueue(text);
            return Task.CompletedTask;
        }
    }
}
