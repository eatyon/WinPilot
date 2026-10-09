using WinPilot.Core;
using WinPilot.Core.Desktop;
using WinPilot.Core.SystemStatus;
using WinPilot.Features.Desktop;
using WinPilot.Features.Notifications.CommandBots;

namespace WinPilot.Test;

public sealed class CommandBotLifecycleTest
{
    [Fact]
    public async Task CommandReceptionDoesNotPublishUnchangedConnectionState()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.Bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = fixture.Bot.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("connected", events.Current.State);
        var next = events.MoveNextAsync().AsTask();
        var replied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bot.Emit(new("origin", "sender", "帮助")
        {
            Reply = (_, _, _) => { replied.TrySetResult(); return Task.CompletedTask; }
        });
        await replied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Logger.Count > 0);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Message == "Test 命令机器人收到消息：帮助");
        await WaitUntil(() => fixture.Logger.Entries.Any(entry => entry.Message == "Test 命令已处理：帮助"));
        Assert.False(next.IsCompleted);
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        Assert.True(await next);
        Assert.Equal("disabled", events.Current.State);
    }

    [Fact]
    public async Task ConnectionFailureIsLoggedOncePerFailedStateAndRecoveryIsLogged()
    {
        await using var fixture = new Fixture();
        fixture.Bot.PublishStatus(new(true, "failed", "配置无效"));
        fixture.Bot.PublishStatus(new(true, "failed", "权限无效"));
        fixture.Bot.PublishStatus(new(true, "connected"));
        fixture.Bot.PublishStatus(new(true, "connected"));
        fixture.Bot.PublishStatus(new(true, "failed", "重连次数已达上限"));

        var warnings = fixture.Logger.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains("配置无效", warnings[0].Message);
        Assert.Contains("重连次数已达上限", warnings[1].Message);
        Assert.Single(fixture.Logger.Entries.Where(entry => entry.Message.EndsWith("连接成功", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DisabledBotDoesNotCreateReceiverOrAcceptCommands()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.StartAsync(CancellationToken.None);
        for (var index = 0; index < 10; index++)
            await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        var replies = 0;
        fixture.Bot.Emit(new("origin", "sender", "帮助")
        {
            Reply = (_, _, _) => { Interlocked.Increment(ref replies); return Task.CompletedTask; }
        });
        Assert.Equal(0, fixture.Bot.Created);
        Assert.Equal(0, fixture.Logger.Count);
        Assert.Equal(0, replies);
    }

    [Fact]
    public async Task DisabledAfterRunningReleasesReceiverAndRejectsFurtherCommands()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.Bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        var received = fixture.Logger.Count;
        fixture.Bot.Emit(new("origin", "sender", "帮助"));
        Assert.Equal(received, fixture.Logger.Count);
        Assert.Equal(fixture.Bot.Created, fixture.Bot.Cleaned);
        Assert.Equal("disabled", fixture.Bot.Status.State);
    }

    [Fact]
    public async Task UnregisteredCommandsDoNotReplyOrLogReception()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.Bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var replies = 0;
        fixture.Bot.Emit(new("origin", "sender", "未注册的命令")
        {
            Reply = (_, _, _) => { Interlocked.Increment(ref replies); return Task.CompletedTask; }
        });
        Assert.Equal(0, fixture.Logger.Count);
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bot.Emit(new("origin", "sender", "帮助")
        {
            Reply = (_, _, _) => { processed.TrySetResult(); return Task.CompletedTask; }
        });
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, replies);
        Assert.True(fixture.Logger.Count > 0);
    }

    [Fact]
    public async Task SystemStatusIsSampledOnlyWhenItsCommandIsReceived()
    {
        var monitor = new CountingSystemMonitor();
        await using var fixture = new Fixture(systemMonitor: monitor);
        await fixture.Bot.StartAsync(CancellationToken.None);
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.Bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, monitor.Captures);
        var replied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bot.Emit(new("origin", "sender", "系统状态")
        {
            Reply = (text, image, _) => { Assert.Equal("", text); Assert.NotNull(image); replied.TrySetResult(); return Task.CompletedTask; }
        });
        await replied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, monitor.Captures);
    }

    [Fact]
    public async Task ScreenshotIsQueuedAndCapturedOnlyAfterCommandReception()
    {
        var provider = new CountingScreenshotProvider();
        await using var fixture = new Fixture(new(provider));
        await fixture.Bot.StartAsync(CancellationToken.None);
        Assert.Equal(0, provider.Captures);
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.Bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, provider.Captures);
        var replied = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bot.Emit(new("origin", "sender", "桌面截图")
        {
            Reply = (text, image, _) =>
            {
                Assert.Equal("", text);
                replied.TrySetResult(image!);
                return Task.CompletedTask;
            }
        });
        Assert.Equal(new byte[] { 1, 2, 3 }, await replied.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, provider.Captures);
    }

    [Fact]
    public async Task HelpDoesNotBlockReceptionAndDisableCancelsReplyAndDropsQueue()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.Bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var help = new CommandBotMessage("chat", "sender", "帮助")
        {
            Reply = async (_, image, token) =>
            {
                Interlocked.Increment(ref calls);
                Assert.NotNull(image);
                sending.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cancelled.TrySetResult(); }
            }
        };
        fixture.Bot.Emit(help);
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Bot.Emit(help);
        var reception = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Bot.Emit(help);
        Assert.True((await reception.WaitAsync(TimeSpan.FromSeconds(3))) > 0);
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(cancelled.Task.IsCompleted);
        Assert.Equal(1, calls);
        Assert.Equal(fixture.Bot.Created, fixture.Bot.Cleaned);
    }

    [Fact]
    public async Task FaultedReceiverDoesNotPreventReconnectOrDisable()
    {
        await using var fixture = new Fixture();
        fixture.Bot.FailNextReceiver = true;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.Bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntil(() => fixture.Bot.Cleaned == fixture.Bot.Created);
        await fixture.Bot.ReconnectAsync(CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Created == 2 && fixture.Bot.Status.State == "connected");
        Assert.True(fixture.Bot.Created > fixture.Bot.Cleaned);
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        Assert.Equal(fixture.Bot.Created, fixture.Bot.Cleaned);
        Assert.Equal("disabled", fixture.Bot.Status.State);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForReceiverCleanupAndCompletesSubscribers()
    {
        await using var fixture = new Fixture();
        fixture.Bot.HoldCleanup = true;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.Bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using var watcher = fixture.Bot.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        var first = fixture.Bot.DisposeAsync().AsTask();
        var second = fixture.Bot.DisposeAsync().AsTask();
        var synchronous = Task.Run(fixture.Bot.Dispose);
        await fixture.Bot.Cancelling.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.True(fixture.Bot.Created > fixture.Bot.Cleaned);
        fixture.Bot.AllowCleanup.TrySetResult();
        await Task.WhenAll(first, second, synchronous).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(fixture.Bot.Created, fixture.Bot.Cleaned);
        Assert.Equal("disabled", fixture.Bot.Status.State);
        Assert.True(await watcher.MoveNextAsync());
        Assert.Equal("disabled", watcher.Current.State);
        Assert.False(await watcher.MoveNextAsync());
        await fixture.Bot.DisposeAsync();
        fixture.Bot.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Bot.ReconnectAsync(CancellationToken.None));
        // 已释放服务从配置协调器移除，后续保存不会尝试重启它。
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile,
            settings => settings with { TelegramBotToken = "new-token" }, CancellationToken.None);
    }

    [Fact]
    public async Task CancellationCallbackFailureStillWaitsAndReleasesReceiver()
    {
        await using var fixture = new Fixture();
        fixture.Bot.ThrowOnCancellation = true;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.Bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Bot.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(fixture.Bot.Created, fixture.Bot.Cleaned);
        Assert.Equal("disabled", fixture.Bot.Status.State);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public PortableDataPaths Paths { get; } = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "winpilot-bot-lifecycle-tests", Guid.NewGuid().ToString("N")));
        public CommandBotConfiguration Configuration { get; } = new();
        public CommandBotTestObserver Logger { get; } = new();
        public TestBot Bot { get; }
        public Fixture(DesktopScreenshotService? screenshots = null, ISystemResourceMonitor? systemMonitor = null)
        {
            new WinPilotSettings { Port = 9123 }.Save(Paths.ConfigurationFile);
            Bot = new(Paths, Configuration, Logger, screenshots, systemMonitor);
        }
        public async ValueTask DisposeAsync()
        {
            Bot.AllowCleanup.TrySetResult();
            await Bot.DisposeAsync();
            Configuration.Dispose();
            Directory.Delete(Paths.ApplicationDirectory, true);
        }
    }

    private sealed class TestBot(PortableDataPaths paths, CommandBotConfiguration configuration, CommandBotTestObserver logger,
        DesktopScreenshotService? screenshots = null, ISystemResourceMonitor? systemMonitor = null)
        : CommandBotService(paths, configuration, logger, "Test", screenshots, systemMonitor)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailNextReceiver;
        public bool HoldCleanup;
        public bool ThrowOnCancellation;
        public int Created;
        public int Cleaned;
        public void Emit(CommandBotMessage message) => Receive(message);
        public void PublishStatus(CommandBotStatus value) => SetStatus(value);
        protected override bool IsEnabled(CommandBotSettings settings) => settings.TelegramEnabled;
        protected override CommandBotSettings WithEnabled(CommandBotSettings settings, bool enabled) => settings with { TelegramEnabled = enabled };
        protected override void ValidateSettings(NotificationSettings settings) { }
        protected override bool SameConnection(NotificationSettings a, NotificationSettings b) => a.TelegramBotToken == b.TelegramBotToken;
        private protected override CommandBotReceiver CreateReceiver()
        {
            Interlocked.Increment(ref Created);
            return new TestReceiver(this, () => Status, SetStatus, Receive);
        }
    }

    private sealed class CountingScreenshotProvider : IDesktopScreenshotProvider
    {
        public int Captures;
        public DesktopScreenshot Capture()
        {
            Interlocked.Increment(ref Captures);
            return new(DateTimeOffset.UtcNow, [1, 2, 3], "image/jpeg");
        }
    }

    private sealed class CountingSystemMonitor : ISystemResourceMonitor
    {
        public int Captures;
        public SystemResourceStatus Capture(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Captures);
            return new(1, 2, [], []);
        }
    }

    private sealed class TestReceiver(TestBot owner, Func<CommandBotStatus> status,
        Action<CommandBotStatus> publish, Action<CommandBotMessage> receive) : CommandBotReceiver(status, publish, receive)
    {
        public override async Task RunAsync(WinPilotSettings settings, CancellationToken token)
        {
            using var registration = token.Register(() =>
            {
                if (owner.ThrowOnCancellation) throw new InvalidOperationException("模拟取消回调异常");
            });
            SetStatus(Status with { State = "connected" });
            owner.Started.TrySetResult();
            try
            {
                if (owner.FailNextReceiver)
                {
                    owner.FailNextReceiver = false;
                    throw new InvalidOperationException("模拟未捕获接收异常");
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                owner.Cancelling.TrySetResult();
                if (owner.HoldCleanup) await owner.AllowCleanup.Task;
                Interlocked.Increment(ref owner.Cleaned);
            }
        }
    }
}
