using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Plugins;
using WinPilot.Features.Plugins;
using static WinPilot.Test.PluginTestSupport;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{
    [Fact]
    public void FailedFinalUnsubscriptionStillRemovesCallback()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            var runtime = new ReentrantStatusRuntime();
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages);
            var old = runtime.Instances.Single();
            var received = new List<string>();
            var subscription = manager.SubscribeState("plugin", received.Add);
            old.OnCount = count =>
            {
                if (count == 0) throw new InvalidOperationException("callback failed");
            };
            subscription.Dispose();
            subscription.Dispose();
            old.Publish("late");
            Assert.Empty(received);
            Assert.Equal([0, 1, 0], old.Counts);
            Assert.Equal(0, StatusSubscriberCount(manager, "plugin"));
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    [Fact]
    public void RuntimeReplacementInsideStateCallbackStopsOldForwarding()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            var runtime = new ReentrantStatusRuntime();
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages);
            using var first = manager.SubscribeState("plugin", json =>
            {
                if (json == "old") manager.Restart("plugin");
            });
            var received = new List<string>();
            using var second = manager.SubscribeState("plugin", received.Add);
            runtime.Instances[0].Publish("old");
            runtime.Instances[1].Publish("new");
            Assert.Equal(["new"], received);
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCountDeliverySkipsStaleNotificationsAndTracksRuntime(bool replaceRuntime)
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            var runtime = new ReentrantStatusRuntime();
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages);
            var old = runtime.Instances.Single();
            using var first = manager.SubscribeState("plugin", _ => { });
            old.OnCount = count =>
            {
                if (count != 2) return;
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            };
            var secondTask = Task.Run(() => manager.SubscribeState("plugin", _ => { }));
            Task? removal = null;
            Task? restart = null;
            IDisposable? second = null;
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                Assert.Equal("0", await Task.Run(() => manager.GetStatus("plugin")).WaitAsync(TimeSpan.FromSeconds(10)));
                removal = Task.Run(first.Dispose);
                Assert.True(SpinWait.SpinUntil(() => StatusSubscriberCount(manager, "plugin") == 1, TimeSpan.FromSeconds(10)));
                if (replaceRuntime)
                {
                    restart = Task.Run(() => manager.Restart("plugin"));
                    Assert.True(SpinWait.SpinUntil(() => manager.GetStatus("plugin") == "1", TimeSpan.FromSeconds(10)));
                }
                Assert.Equal([0, 1, 2], old.Counts);
            }
            finally
            {
                release.Set();
                second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (removal is not null) await removal.WaitAsync(TimeSpan.FromSeconds(10));
                if (restart is not null) await restart.WaitAsync(TimeSpan.FromSeconds(10));
                second?.Dispose();
            }
            if (replaceRuntime)
            {
                Assert.Equal([0, 1, 2], old.Counts);
                Assert.Equal([1, 0], runtime.Instances[^1].Counts);
            }
            else Assert.Equal([0, 1, 2, 1, 0], old.Counts);
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    [Fact]
    public void ReentrantSubscriptionsDiscardPendingOldCountsAndEndAtZero()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            var runtime = new ReentrantStatusRuntime();
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages);
            var instance = runtime.Instances.Single();
            var delivered = new List<int>();
            var active = false;
            var expanded = false;
            instance.OnCount = count =>
            {
                Assert.False(active);
                active = true;
                try
                {
                    delivered.Add(count);
                    if (count == 1 && !expanded)
                    {
                        expanded = true;
                        using var second = manager.SubscribeState("plugin", _ => { });
                        using var third = manager.SubscribeState("plugin", _ => { });
                    }
                }
                finally { active = false; }
            };
            using (manager.SubscribeState("plugin", _ => { })) { }
            Assert.Equal([1, 1, 0], delivered);
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    [Fact]
    public void ReentrantSubscriptionFailureRollsBackBeforeFinalCount()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            var runtime = new ReentrantStatusRuntime();
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages);
            var instance = runtime.Instances.Single();
            var delivered = new List<int>();
            IDisposable? nested = null;
            var attempted = false;
            instance.OnCount = count =>
            {
                delivered.Add(count);
                if (count == 1 && !attempted)
                {
                    attempted = true;
                    nested = manager.SubscribeState("plugin", _ => { });
                }
                if (count == 2) throw new InvalidOperationException("callback failed");
            };
            try
            {
                Assert.Throws<InvalidOperationException>(() => manager.SubscribeState("plugin", _ => { }));
                Assert.Equal(0, delivered[^1]);
            }
            finally { nested?.Dispose(); }
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    [Fact]
    public void ReplacedRuntimeCannotForwardLateState()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            var runtime = new ReentrantStatusRuntime();
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages);
            using var subscription = manager.SubscribeState("plugin", _ => { });
            var old = runtime.Instances.Single();
            manager.Restart("plugin");
            var received = new List<string>();
            using var current = manager.SubscribeState("plugin", received.Add);
            old.Publish("old");
            runtime.Instances[^1].Publish("new");
            Assert.Equal(["new"], received);
            Assert.Equal([0, 1], old.Counts);
            Assert.Equal([1, 2], runtime.Instances[^1].Counts);
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    private sealed class ReentrantStatusRuntime : IPluginRuntime
    {
        public List<Instance> Instances { get; } = [];
        public IPluginRuntimeInstance Start(PluginInfo plugin)
        {
            var instance = new Instance { StatusJson = Instances.Count.ToString() };
            Instances.Add(instance);
            return instance;
        }
        public sealed class Instance : IPluginRuntimeInstance, IPluginRuntimeStatus, IPluginRuntimeStatusSubscriptions
        {
            public string? StatusJson { get; init; }
            public event Action<string>? StateChanged;
            public Action<int>? OnCount { get; set; }
            public List<int> Counts { get; } = [];
            public void SetStatusSubscriberCount(int count)
            {
                Counts.Add(count);
                OnCount?.Invoke(count);
            }
            public void Publish(string json) => StateChanged?.Invoke(json);
            public void Dispose() { }
        }
    }

    private static int StatusSubscriberCount(PluginManager manager, string id)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gate = typeof(PluginManager).GetField("gate", flags)!.GetValue(manager)!;
        lock (gate)
        {
            var subscribers = (Dictionary<string, List<Action<string>>>)typeof(PluginManager).GetField("stateSubscribers", flags)!.GetValue(manager)!;
            return subscribers.TryGetValue(id, out var callbacks) ? callbacks.Count : 0;
        }
    }
}
