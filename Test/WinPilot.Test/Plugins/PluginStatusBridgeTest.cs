using WinPilot.Features.Plugins;

namespace WinPilot.Test;

public sealed class PluginStatusBridgeTest
{
    [Fact]
    public async Task ConcurrentIdenticalStatesAreDeliveredOnce()
    {
        using var bridge = new PluginStatusBridge();
        using var start = new Barrier(3);
        var notifications = 0;
        bridge.StateChanged += _ => Interlocked.Increment(ref notifications);
        var first = Task.Run(Publish);
        var second = Task.Run(Publish);
        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, notifications);
        Assert.Equal("{}", bridge.StatusJson);

        void Publish()
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            bridge.Publish("{}");
        }
    }

    [Fact]
    public async Task ConcurrentPublishDeliversInCommitOrderWithoutOverlappingCallbacks()
    {
        using var bridge = new PluginStatusBridge();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var delivered = new List<string>();
        bridge.StateChanged += json =>
        {
            if (json == "1")
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            lock (delivered) delivered.Add(json);
        };
        var first = Task.Run(() => bridge.Publish("1"));
        Task? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run(() => bridge.Publish("2"));
            Assert.True(SpinWait.SpinUntil(() => bridge.StatusJson == "2", TimeSpan.FromSeconds(10)));
            Assert.False(second.IsCompleted);
            lock (delivered) Assert.Empty(delivered);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(["1", "2"], delivered);
        Assert.Equal(delivered[^1], bridge.StatusJson);
    }

    [Fact]
    public void ReentrantPublishFinishesCurrentDeliveryBeforeNextStateAndDeduplicates()
    {
        using var bridge = new PluginStatusBridge();
        var delivered = new List<string>();
        bridge.StateChanged += json =>
        {
            delivered.Add("first:" + json);
            if (json == "1")
            {
                bridge.Publish("2");
                bridge.Publish("2");
            }
        };
        bridge.StateChanged += json => delivered.Add("second:" + json);
        bridge.Publish("1");
        Assert.Equal(["first:1", "second:1", "first:2", "second:2"], delivered);
        Assert.Equal("2", bridge.StatusJson);
    }

    [Fact]
    public void FailedDeliveryDoesNotStrandReentrantPublish()
    {
        using var bridge = new PluginStatusBridge();
        var delivered = new List<string>();
        var failure = new InvalidOperationException("callback failed");
        bridge.StateChanged += json =>
        {
            delivered.Add(json);
            if (json == "1")
            {
                bridge.Publish("2");
                throw failure;
            }
        };
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => bridge.Publish("1")));
        bridge.Publish("3");
        Assert.Equal(["1", "2", "3"], delivered);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CrossThreadReentrantPublishDoesNotWaitForItsCallingCallback(bool newThread, bool failNested)
    {
        using var bridge = new PluginStatusBridge();
        using var publishAllowed = new ManualResetEventSlim();
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var workerReturned = new ManualResetEventSlim();
        Exception? workerFailure = null;
        var delivered = new List<string>();
        var failure = new InvalidOperationException("nested callback failed");
        Task? worker = null;
        Thread? thread = null;
        bridge.StateChanged += json =>
        {
            delivered.Add("first:" + json);
            if (json == "2" && failNested) throw failure;
            if (json != "1") return;
            if (newThread)
            {
                thread = new Thread(PublishNested) { IsBackground = true };
                thread.Start();
            }
            else worker = Task.Run(PublishNested);
            Assert.True(workerReturned.Wait(TimeSpan.FromSeconds(10)));
            Assert.Null(workerFailure);
            Assert.Equal(["first:1"], delivered);
        };
        bridge.StateChanged += json => delivered.Add("second:" + json);
        var outer = Task.Run(() => Record.Exception(() => bridge.Publish("1")));
        try
        {
            await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("1", bridge.StatusJson);
            Assert.False(outer.IsCompleted);
            publishAllowed.Set();
            var error = await outer.WaitAsync(TimeSpan.FromSeconds(20));
            if (failNested) Assert.Same(failure, error);
            else Assert.Null(error);
            Assert.Equal(failNested
                ? ["first:1", "second:1", "first:2"]
                : ["first:1", "second:1", "first:2", "second:2"], delivered);
            Assert.Equal("2", bridge.StatusJson);
        }
        finally
        {
            publishAllowed.Set();
            await outer.WaitAsync(TimeSpan.FromSeconds(20));
            if (worker is not null) await worker.WaitAsync(TimeSpan.FromSeconds(10));
            if (thread is not null) Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        }

        void PublishNested()
        {
            try
            {
                workerEntered.TrySetResult();
                Assert.True(publishAllowed.Wait(TimeSpan.FromSeconds(10)));
                bridge.Publish("2");
                bridge.Publish("2");
            }
            catch (Exception exception) { workerFailure = exception; }
            finally { workerReturned.Set(); }
        }
    }

    [Fact]
    public async Task PublishFromCompletedCallbackContextHasItsOwnCompletionAndFailure()
    {
        using var bridge = new PluginStatusBridge();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("late callback failed");
        var delivered = new List<string>();
        Task<Exception>? worker = null;
        bridge.StateChanged += json =>
        {
            delivered.Add(json);
            if (json == "2") throw failure;
            worker = Task.Run(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return Record.Exception(() => bridge.Publish("2"));
            });
        };
        bridge.Publish("1");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.TrySetResult();
            Assert.Same(failure, await worker!.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(["1", "2"], delivered);
            Assert.Equal("2", bridge.StatusJson);
        }
        finally
        {
            release.TrySetResult();
            if (worker is not null) await worker.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
