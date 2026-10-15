using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinPilot.Core;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;
using WinPilot.Features.Security;

namespace WinPilot.Test;

public sealed class SecurityNotificationLifetimeTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinPilot.SecurityNotificationLifetime.Test", Guid.NewGuid().ToString("N"));
    private static SecurityNotificationSettings Settings => new() { Events = [SecurityEventType.LoginSucceeded], Channel = "telegram" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalReleasesStateGateBeforeWaitingForStoppingRegistration(bool asynchronous)
    {
        using var stopping = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposing = new ManualResetEventSlim();
        var sender = new Sender(_ => throw new InvalidOperationException("不应投递通知"));
        var notifier = new SecurityEventNotifier(PortableDataPaths.Create(root), sender, new NotificationLogger());
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var gate = typeof(SecurityEventNotifier).GetField("deliveryGate", flags)!.GetValue(notifier)!;
        var callbackAcquired = false;
        var callback = stopping.Token.Register(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("停止回调握手未解除");
            callbackAcquired = Monitor.TryEnter(gate);
            if (!callbackAcquired) return;
            try { typeof(SecurityEventNotifier).GetMethod("RequestStop", flags)!.Invoke(notifier, null); }
            finally { Monitor.Exit(gate); }
        });
        typeof(SecurityEventNotifier).GetField("stoppingRegistration", flags)!.SetValue(notifier, callback);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            disposing.Set();
            try
            {
                DisposeNotifier(notifier, asynchronous);
                completed.TrySetResult();
            }
            catch (Exception exception) { completed.TrySetException(exception); }
        }) { IsBackground = true };
        var cancelled = Task.Run(stopping.Cancel);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            thread.Start();
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => thread.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(5)));
            var available = Monitor.TryEnter(gate);
            if (available) Monitor.Exit(gate);
            Task? concurrent = null;
            if (available)
            {
                concurrent = notifier.DisposeAsync().AsTask();
                Assert.Same(typeof(SecurityEventNotifier).GetField("disposal", flags)!.GetValue(notifier), concurrent);
                Assert.False(concurrent.IsCompleted);
            }
            release.Set();
            await Task.WhenAll(cancelled, completed.Task, concurrent ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(available, "释放取消注册时仍持有停止回调所需的状态锁");
            Assert.True(callbackAcquired);
            Assert.False(sender.Disposed);
            Assert.Empty(Deliveries(notifier));
            await notifier.DisposeAsync();
        }
        finally
        {
            release.Set();
            await cancelled.WaitAsync(TimeSpan.FromSeconds(5));
            if (!thread.ThreadState.HasFlag(System.Threading.ThreadState.Unstarted)) await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await notifier.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopCancelsDeliveriesWaitsForFinallyAndKeepsEventLogs(bool throwingCallback)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("测试通知取消回调失败");
        var sender = new Sender(async token =>
        {
            using var callback = token.Register(() => { if (throwingCallback) throw failure; });
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
            return [];
        });
        var logger = new NotificationLogger();
        await using var notifier = new SecurityEventNotifier(PortableDataPaths.Create(root), sender, logger);
        Task? stopping = null;
        try
        {
            notifier.Record(SecurityEventType.LoginSucceeded, notificationSettings: Settings);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = notifier.StopAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            notifier.Record(SecurityEventType.LoginSucceeded, notificationSettings: Settings);
            Assert.Equal(1, sender.Calls);
            Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Information));
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(Deliveries(notifier));
            await notifier.StopAsync();
            await notifier.DisposeAsync();
            var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
            if (throwingCallback) Assert.Same(failure, Assert.Single(Assert.IsType<AggregateException>(Assert.Single(warnings).Error).InnerExceptions));
            else Assert.Empty(warnings);
        }
        finally { release.TrySetResult(); if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task SynchronouslyCompletedDeliveriesAreNotRetained()
    {
        var sender = new Sender(_ => Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new("telegram", true, "")]));
        await using var notifier = new SecurityEventNotifier(PortableDataPaths.Create(root), sender, new NotificationLogger());
        for (var index = 0; index < 100; index++)
        {
            notifier.Record(SecurityEventType.LoginSucceeded, notificationSettings: Settings);
            Assert.Empty(Deliveries(notifier));
        }
        Assert.Equal(100, sender.Calls);
        await notifier.StopAsync();
        notifier.Record(SecurityEventType.LoginSucceeded, notificationSettings: Settings);
        Assert.Equal(100, sender.Calls);
        Assert.False(sender.Disposed);
    }

    [Fact]
    public async Task StopWinningRegistrationRacePreventsNetworkDelivery()
    {
        var sender = new Sender(_ => Task.FromResult<IReadOnlyList<NotificationChannelResult>>([]));
        await using var notifier = new SecurityEventNotifier(PortableDataPaths.Create(root), sender, new NotificationLogger());
        var gate = typeof(SecurityEventNotifier).GetField("deliveryGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(notifier)!;
        using var entered = new ManualResetEventSlim();
        Task record;
        Task stop;
        lock (gate)
        {
            record = Task.Run(() => { entered.Set(); notifier.Record(SecurityEventType.LoginSucceeded, notificationSettings: Settings); });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            stop = notifier.StopAsync();
        }
        await Task.WhenAll(record, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, sender.Calls);
        Assert.Empty(Deliveries(notifier));
    }

    [Fact]
    public async Task HostShutdownCancelsBeforeNotificationDependencyIsDisposed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new Sender(async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
            return [];
        });
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(PortableDataPaths.Create(root));
        builder.Services.AddSingleton<INotificationService>(_ => sender);
        builder.Services.AddSingleton<SecurityEventNotifier>();
        var host = builder.Build();
        Task? disposal = null;
        try
        {
            await host.StartAsync();
            host.Services.GetRequiredService<SecurityEventNotifier>().Record(SecurityEventType.LoginSucceeded, notificationSettings: Settings);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = ((IAsyncDisposable)host).DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(sender.Disposed);
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(sender.Disposed);
        }
        finally { release.TrySetResult(); await (disposal ?? ((IAsyncDisposable)host).DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static void DisposeNotifier(SecurityEventNotifier notifier, bool asynchronous)
    {
        if (asynchronous) notifier.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else notifier.Dispose();
    }

    private static IEnumerable<Task> Deliveries(SecurityEventNotifier notifier) =>
        (HashSet<Task>)typeof(SecurityEventNotifier).GetField("deliveries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(notifier)!;

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Sender(Func<CancellationToken, Task<IReadOnlyList<NotificationChannelResult>>> send) : INotificationService, IDisposable
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal bool Disposed { get; private set; }
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default,
            string? channel = null, bool ignoreChannelEnabled = false)
        {
            Assert.False(Disposed);
            Assert.Equal("telegram", channel);
            Assert.True(ignoreChannelEnabled);
            Interlocked.Increment(ref calls);
            return send(cancellationToken);
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class NotificationLogger : ILogger<SecurityEventNotifier>
    {
        internal ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((level, formatter(state, exception), exception));
    }
}
