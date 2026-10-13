using WinPilot.Core;
using WinPilot.Features.Plugins;
using WinPilot.Core.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (UnauthorizedAccessException) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(100); }
        }
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static PluginManager CreatePluginManager(PortableDataPaths paths, IPluginRuntime runtime, ILogger<PluginManager>? logger = null) =>
        new(paths, logger ?? NullLogger<PluginManager>.Instance, runtime,
            new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance));

    private static void WritePlugin(PortableDataPaths paths, string directoryName, string id, string source)
    {
        var directory = Path.Combine(paths.PluginsDirectory, directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.dll"), source);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{ \"id\": \"" + id + "\", \"name\": \"" + id + "\", \"version\": \"1.0.0\", \"main\": \"plugin.dll\" }");
        var dataDirectory = Path.Combine(paths.PluginDataDirectory, id);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "plugin.json"), "{ \"enabled\": true }");
    }

    private sealed class RecordingPluginRuntime : IPluginRuntime
    {
        public List<string> StartedIds { get; } = [];
        public List<PluginInfo> StartedPlugins { get; } = [];
        public List<string> DisposedIds { get; } = [];

        public IPluginRuntimeInstance Start(PluginInfo plugin)
        {
            StartedIds.Add(plugin.Id);
            StartedPlugins.Add(plugin);
            return new Instance(plugin.Id, DisposedIds);
        }

        private sealed class Instance(string pluginId, List<string> disposedIds) : IPluginRuntimeInstance
        {
            public void Dispose() => disposedIds.Add(pluginId);
        }
    }

    private sealed class WaitingPluginRuntime(bool failStart) : IPluginRuntime, IDisposable
    {
        private readonly ManualResetEventSlim release = new();
        public bool BlockStart { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPluginRuntimeInstance Start(PluginInfo plugin)
        {
            if (BlockStart)
            {
                Started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("测试未释放插件启动");
                if (failStart) throw new InvalidOperationException("测试启动失败");
            }
            return new Instance();
        }

        public void Release() => release.Set();
        public void Dispose() => release.Dispose();

        private sealed class Instance : IPluginRuntimeInstance
        {
            public void Dispose() { }
        }
    }

    private sealed class PagePluginRuntime : IPluginRuntime
    {
        public IPluginRuntimeInstance Start(PluginInfo plugin) => new Instance();

        private sealed class Instance : IPluginRuntimeInstance, IPluginRuntimePageFeatures
        {
            public void OpenPage(string featureId, WinPilot.Features.Plugins.IPluginPageSession session) =>
                session.RegisterMessageHandler((_, _) => Task.FromResult("reply"));
            public void Dispose() { }
        }
    }

    private sealed class StatusSubscriberTrackingRuntime : IPluginRuntime
    {
        public List<Instance> Instances { get; } = [];
        public IPluginRuntimeInstance Start(PluginInfo plugin)
        {
            var instance = new Instance();
            Instances.Add(instance);
            return instance;
        }

        public sealed class Instance : IPluginRuntimeInstance, IPluginRuntimeStatusSubscriptions
        {
            public List<int> Counts { get; } = [];
            public void SetStatusSubscriberCount(int count) => Counts.Add(count);
            public void Dispose() { }
        }
    }

    private sealed class FixedPluginSecurity(bool enabled) : IPluginHostSecurityApi
    {
        public bool SafeModeEnabled { get; } = enabled;
    }

}
