using WinPilot.Features.Plugins;
using WinPilot.Core.Plugins;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{
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
}
