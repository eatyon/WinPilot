using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Plugins;
using WinPilot.Features.Plugins;

using static WinPilot.Test.PluginTestSupport;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{
    [Theory]
    [InlineData("{\"id\":null,\"name\":\"Bad\",\"version\":\"1.0.0\",\"main\":\"plugin.dll\"}", null)]
    [InlineData("{\"id\":\"bad\",\"name\":\"Bad\",\"version\":\"1.0.0\",\"main\":\"plugin.dll\",\"authors\":[null]}", null)]
    [InlineData("{\"id\":\"bad\",\"name\":\"Bad\",\"version\":\"1.0.0\",\"main\":\"plugin.dll\",\"features_ui\":\"features.json\"}", "{\"features\":[{}]}")]
    public void InvalidDirectoryDoesNotHideOtherPlugins(string manifest, string? features)
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(paths, "valid", "valid", "");
            var invalid = Path.Combine(paths.PluginsDirectory, "bad");
            Directory.CreateDirectory(invalid);
            File.WriteAllText(Path.Combine(invalid, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(invalid, "manifest.json"), manifest);
            if (features is not null) File.WriteAllText(Path.Combine(invalid, "features.json"), features);
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
            Assert.Equal("valid", Assert.Single(manager.GetPlugins()).Id);
            manager.Stop("valid");
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    [Fact]
    public void FailedStartupCallbackDisposesAndRemovesRuntime()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            var runtime = new FailingSubscriptionsRuntime();
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages);
            Assert.Equal(PluginState.Failed, manager.GetPlugin("plugin").State);
            Assert.Equal(1, runtime.Disposed);
            manager.Stop("plugin");
            Assert.Equal(1, runtime.Disposed);
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    [Fact]
    public void FailedFileDeletionRetainsPluginForRetry()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
            using (File.Open(Path.Combine(paths.PluginsDirectory, "plugin", "plugin.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.NotNull(Record.Exception(() => manager.Delete("plugin", false)));
                Assert.Single(manager.GetPlugins());
            }
            manager.Delete("plugin", false);
            Assert.Empty(manager.GetPlugins());
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    [Fact]
    public async Task StoppingWaitsForPageInitializationAndClosesItsSession()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        using var release = new ManualResetEventSlim();
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        Task<PluginManager.PluginPageFeatureSession>? opening = null;
        Task? stopping = null;
        PluginManager? manager = null;
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            var directory = Path.Combine(paths.PluginsDirectory, "plugin");
            Directory.CreateDirectory(Path.Combine(directory, "web"));
            File.WriteAllText(Path.Combine(directory, "web", "index.js"), "export default {}");
            File.WriteAllText(Path.Combine(directory, "features.json"), """{"features":[{"id":"page","name":"Page","mode":"page","entry":"index.js"}]}""");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), """{"id":"plugin","name":"Plugin","version":"1.0.0","main":"plugin.dll","features_ui":"features.json"}""");
            var runtime = new BlockingPageRuntime(release);
            manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, runtime, sessions);
            opening = Task.Run(() => manager.OpenPageFeatureSession("plugin", "page"));
            await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stopping = Task.Run(() => manager.Stop("plugin"));
            await Task.Delay(50);
            Assert.False(stopping.IsCompleted);
            release.Set();
            var page = await opening.WaitAsync(TimeSpan.FromSeconds(3));
            await stopping.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Throws<KeyNotFoundException>(() => sessions.Touch(page.Id));
        }
        finally
        {
            try
            {
                release.Set();
                try { if (opening is not null) await opening.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            finally
            {
                try { manager?.Dispose(); }
                finally
                {
                    try { sessions.Dispose(); }
                    finally { TryDeleteDirectory(paths.ApplicationDirectory); }
                }
            }
        }
    }

    [Fact]
    public void FailedSubscriptionDoesNotRetainItsCallback()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N")));
        try
        {
            WritePlugin(paths, "plugin", "plugin", "");
            var runtime = new FailingSubscriptionsRuntime(true);
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages);
            Assert.Throws<InvalidOperationException>(() => manager.SubscribeState("plugin", _ => { }));
            Assert.Throws<InvalidOperationException>(() => manager.SubscribeState("plugin", _ => { }));
            Assert.Equal(new[] { 0, 1, 0, 1, 0 }, runtime.Counts);
            manager.Stop("plugin");
        }
        finally { TryDeleteDirectory(paths.ApplicationDirectory); }
    }

    private sealed class FailingSubscriptionsRuntime(bool subscriptionsOnly = false) : IPluginRuntime
    {
        private readonly bool subscriptionsOnly = subscriptionsOnly;
        public int Disposed;
        public List<int> Counts { get; } = [];
        public IPluginRuntimeInstance Start(PluginInfo plugin) => new Instance(this);
        private sealed class Instance(FailingSubscriptionsRuntime owner) : IPluginRuntimeInstance, IPluginRuntimeStatusSubscriptions
        {
            public void SetStatusSubscriberCount(int count)
            {
                owner.Counts.Add(count);
                if (!owner.subscriptionsOnly || count > 0) throw new InvalidOperationException("callback failed");
            }
            public void Dispose() => owner.Disposed++;
        }
    }

    private sealed class BlockingPageRuntime(ManualResetEventSlim release) : IPluginRuntime
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IPluginRuntimeInstance Start(PluginInfo plugin) => new Instance(this, release);
        private sealed class Instance(BlockingPageRuntime owner, ManualResetEventSlim release) : IPluginRuntimeInstance, IPluginRuntimePageFeatures
        {
            public void Dispose() { }
            public void OpenPage(string featureId, IPluginPageSession session)
            {
                owner.Entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
                session.RegisterMessageHandler((_, _) => Task.FromResult("{}"));
            }
        }
    }
}
