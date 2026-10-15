using System.Reflection;
using Microsoft.Extensions.Logging;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class PluginCommandExecutionLifetimeTest
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TimeoutEndsCallerWaitButTrackingLastsUntilCallbackFinishes(bool wildcard, bool failLate)
    {
        var logger = new CommandBotTestObserver<PluginCommandRegistry>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, logger);
        using var scope = registry.CreateScope("test", "Test");
        var entered = Signal();
        var release = Signal();
        var finished = Signal();
        var refused = Signal();
        scope.Register(new(wildcard ? "*" : "慢命令", "Slow"), async (context, _) =>
        {
            entered.TrySetResult();
            try
            {
                await release.Task;
                var error = await Record.ExceptionAsync(() => context.Reply.ReplyTextAsync("迟到回复"));
                Assert.True(error is OperationCanceledException or ObjectDisposedException);
                refused.TrySetResult();
                if (failLate) throw new IOException("迟到执行失败", new InvalidOperationException("内部原因"));
            }
            finally { finished.TrySetResult(); }
        });
        var clock = new DeadlineClock();
        var processorLog = new CommandBotTestObserver();
        using var processor = new CommandProcessor("Test", new(), processorLog, pluginCommands: registry, clock: clock);
        var sends = 0;
        var processing = processor.ProcessAsync(new("chat", "sender", wildcard ? "未知命令" : "慢命令")
        { Reply = (_, _, _) => { sends++; return Task.CompletedTask; } }, "", CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _ = Assert.Single(Executions(registry));
            var actual = Executions(registry).Single();
            clock.Expire();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(actual.IsCompleted);
            _ = Assert.Single(Executions(registry));
            Assert.Contains(processorLog.Entries, entry => entry.Message.Contains("超时"));
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await refused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Idle(registry);
            Assert.Equal(0, sends);
            if (failLate)
            {
                var warning = Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Warning));
                Assert.Contains("处理失败", warning.Message);
                var diagnostic = Assert.Single(logger.Diagnostics);
                Assert.Contains("迟到执行失败", diagnostic);
                Assert.Contains("内部原因", diagnostic);
            }
            else Assert.Empty(logger.Diagnostics);
        }
        finally { release.TrySetResult(); await processing.WaitAsync(TimeSpan.FromSeconds(5)); await Idle(registry); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CooperativeCancellationCompletesAndUnregistersCallback(bool wildcard)
    {
        var logger = new CommandBotTestObserver<PluginCommandRegistry>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, logger);
        using var scope = registry.CreateScope("test", "Test");
        var entered = Signal();
        scope.Register(new(wildcard ? "*" : "取消命令", "Cancel"), async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        using var cancellation = new CancellationTokenSource();
        using var processor = new CommandProcessor("Test", new(), new CommandBotTestObserver(), pluginCommands: registry);
        var pending = processor.ProcessAsync(new("chat", "sender", wildcard ? "未知命令" : "取消命令"), "", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Idle(registry);
        Assert.Empty(logger.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveSendKeepsReplyAndProcessorResourcesUntilTransportFinishes(bool wildcard)
    {
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand);
        using var scope = registry.CreateScope("test", "Test");
        PluginCommandReply? reply = null;
        scope.Register(new(wildcard ? "*" : "发送命令", "Send"), async (context, _) =>
        { reply = Assert.IsType<PluginCommandReply>(context.Reply); await reply.ReplyTextAsync("消息"); });
        var entered = Signal();
        var release = Signal();
        var clock = new DeadlineClock();
        var handler = new ClientHandler();
        using var processor = new CommandProcessor("Test", new(), new CommandBotTestObserver(), pluginCommands: registry, clock: clock);
        typeof(CommandProcessor).GetField("client", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(processor, new HttpClient(handler));
        var pending = processor.ProcessAsync(new("chat", "sender", wildcard ? "未知命令" : "发送命令")
        { Reply = async (_, _, _) => { entered.TrySetResult(); await release.Task; } }, "", CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Expire();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            processor.Dispose();
            reply!.Dispose();
            var source = (CancellationTokenSource)typeof(PluginCommandReply).GetField("cancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reply)!;
            Assert.True(source.Token.IsCancellationRequested);
            Assert.False(handler.Disposed);
            _ = Assert.Single(Executions(registry));
            release.TrySetResult();
            await Idle(registry);
            Assert.True(handler.Disposed);
            Assert.Throws<ObjectDisposedException>(() => source.Token);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => reply.ReplyTextAsync("迟到回复"));
        }
        finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); await Idle(registry); }
    }

    [Fact]
    public async Task TrackingDoesNotLimitOrDropConcurrentCommands()
    {
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand);
        using var scope = registry.CreateScope("test", "Test");
        var release = Signal();
        var entered = Signal();
        var calls = 0;
        scope.Register(new("并发命令", "Concurrent"), async (_, _) =>
        { if (Interlocked.Increment(ref calls) == 3) entered.TrySetResult(); await release.Task; });
        using var processor = new CommandProcessor("Test", new(), new CommandBotTestObserver(), pluginCommands: registry);
        var pending = Enumerable.Range(0, 3).Select(_ => processor.ProcessAsync(new("chat", "sender", "并发命令"), "", CancellationToken.None)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, Executions(registry).Length);
            release.TrySetResult();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
            await Idle(registry);
            Assert.Equal(3, calls);
        }
        finally { release.TrySetResult(); await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)); await Idle(registry); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task[] Executions(PluginCommandRegistry registry)
    {
        var gate = typeof(PluginCommandRegistry).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(registry)!;
        lock (gate) return ((HashSet<Task>)typeof(PluginCommandRegistry).GetField("executions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(registry)!).ToArray();
    }
    private static async Task Idle(PluginCommandRegistry registry)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Executions(registry).Length != 0) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
    private sealed class ClientHandler : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class DeadlineClock : TimeProvider
    {
        private ControlledTimer? timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(60), dueTime);
            return timer = new ControlledTimer(callback, state);
        }
        internal void Expire() => timer!.Fire();
        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
