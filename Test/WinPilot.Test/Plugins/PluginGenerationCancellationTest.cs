using System.Reflection;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class PluginGenerationCancellationTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCallbacksCanReenterRegistryFromAnotherThread(bool invalidate)
    {
        var registry = new PluginTaskTriggerRegistry();
        var previous = registry.BeginGeneration("test");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registration = previous.Token.Register(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var retiring = Task.Run(() =>
        {
            if (invalidate) registry.Invalidate("test", previous.Generation);
            else registry.BeginGeneration("test");
        });
        Task? probing = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            probing = Task.Run(() =>
            {
                _ = registry.GetAll();
                Assert.Throws<InvalidOperationException>(() => registry.Register("test", "Test", previous.Generation, [new("old", "Old")]));
                var other = registry.BeginGeneration("other");
                using var otherRegistration = registry.Register("other", "Other", other.Generation, [new("trigger", "Trigger")]);
                registry.Invalidate("other", other.Generation);
            });
            await probing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(retiring, probing ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            registry.Invalidate("test", CurrentGeneration(registry));
        }
        Assert.True(previous.Token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => previous.Token.WaitHandle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCreationIsRejectedWithoutChangingPendingGenerationAndCanRecover(bool invalidatePending)
    {
        var registry = new PluginTaskTriggerRegistry();
        var previous = registry.BeginGeneration("test");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var callback = previous.Token.Register(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var first = Task.Run(() => registry.BeginGeneration("test"));
        Task<Exception>? rejected = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            rejected = Task.Run(() => Record.Exception(() => registry.BeginGeneration("TEST")));
            var exception = Assert.IsType<InvalidOperationException>(await rejected.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("插件正在取消交接，请在交接完成后重试", exception.Message);
            Assert.Equal(2, CurrentGeneration(registry));
            Assert.Throws<InvalidOperationException>(() => registry.Register("test", "Test", 2, [new("trigger", "Trigger")]));
            registry.Invalidate("test", previous.Generation);
            if (invalidatePending) registry.Invalidate("test", 2);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (rejected is not null) await rejected.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var current = await first;
        Assert.Equal(2, current.Generation);
        Assert.True(previous.Token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => previous.Token.WaitHandle);
        if (invalidatePending)
        {
            Assert.True(current.Token.IsCancellationRequested);
            Assert.Throws<InvalidOperationException>(() => registry.Register("test", "Test", 2, [new("trigger", "Trigger")]));
        }
        else
        {
            Assert.False(current.Token.IsCancellationRequested);
            using var registration = registry.Register("test", "Test", current.Generation, [new("trigger", "Trigger")]);
            registry.Invalidate("test", previous.Generation);
            Assert.Single(registry.GetAll());
        }
        registry.Invalidate("test", current.Generation);
        var resumed = registry.BeginGeneration("test");
        Assert.Equal(3, resumed.Generation);
        Assert.False(resumed.Token.IsCancellationRequested);
        using var resumedRegistration = registry.Register("test", "Test", resumed.Generation, [new("resumed", "Resumed")]);
        registry.Invalidate("test", current.Generation);
        Assert.Equal("resumed", Assert.Single(registry.GetAll()).TriggerId);
        registry.Invalidate("test", resumed.Generation);
        Assert.Equal(3, CurrentGeneration(registry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossThreadCancellationReentryIsRejectedAndHandoffCanRecover(bool invalidate)
    {
        var registry = new PluginTaskTriggerRegistry();
        var previous = registry.BeginGeneration("test");
        using var workerEntered = new ManualResetEventSlim();
        Thread? worker = null;
        Exception? callbackFailure = null;
        Exception? rejection = null;
        using var callback = previous.Token.Register(() =>
        {
            worker = new Thread(() =>
            {
                workerEntered.Set();
                rejection = Record.Exception(() => registry.BeginGeneration("TEST"));
            }) { IsBackground = true };
            worker.Start();
            try
            {
                if (!workerEntered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("重入线程未进入");
                if (!worker.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("重入线程等待取消回调结束，形成循环等待");
            }
            catch (Exception exception) { callbackFailure = exception; }
        });
        try
        {
            await Task.Run(() =>
            {
                if (invalidate) registry.Invalidate("test", previous.Generation);
                else registry.BeginGeneration("test");
            }).WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (worker is not null) Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }
        Assert.Null(callbackFailure);
        Assert.Equal("插件正在取消交接，请在交接完成后重试", Assert.IsType<InvalidOperationException>(rejection).Message);
        var generation = invalidate ? 1 : 2;
        Assert.Equal(generation, CurrentGeneration(registry));
        Assert.True(previous.Token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => previous.Token.WaitHandle);
        var resumed = registry.BeginGeneration("test");
        Assert.Equal(generation + 1, resumed.Generation);
        Assert.False(resumed.Token.IsCancellationRequested);
        using var registration = registry.Register("test", "Test", resumed.Generation, [new("resumed", "Resumed")]);
        registry.Invalidate("test", previous.Generation);
        Assert.Equal("resumed", Assert.Single(registry.GetAll()).TriggerId);
        registry.Invalidate("test", resumed.Generation);
    }

    [Fact]
    public void SameGenerationCancellationCallbackCannotWaitForItsOwnHandoff()
    {
        var registry = new PluginTaskTriggerRegistry();
        var previous = registry.BeginGeneration("test");
        Exception? rejection = null;
        using var callback = previous.Token.Register(() =>
            rejection = Record.Exception(() => registry.BeginGeneration("test")));
        var current = registry.BeginGeneration("test");
        Assert.Equal("插件正在取消交接，请在交接完成后重试", Assert.IsType<InvalidOperationException>(rejection).Message);
        Assert.Equal(2, current.Generation);
        Assert.False(current.Token.IsCancellationRequested);
        registry.Invalidate("test", current.Generation);
    }

    private static long CurrentGeneration(PluginTaskTriggerRegistry registry)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var gate = typeof(PluginTaskTriggerRegistry).GetField("gate", flags)!.GetValue(registry)!;
        lock (gate) return ((Dictionary<string, long>)typeof(PluginTaskTriggerRegistry).GetField("generations", flags)!.GetValue(registry)!)["test"];
    }
}
