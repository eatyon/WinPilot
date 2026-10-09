using WinPilot.Core.Plugins;

namespace WinPilot.Features.Plugins;

public interface IPluginRuntime
{
    IPluginRuntimeInstance Start(PluginInfo plugin);
}

public interface IPluginRuntimeInstance : IDisposable
{
}

public interface IPluginRuntimeStatus
{
    string? StatusJson { get; }
    event Action<string>? StateChanged;
}

public interface IPluginRuntimeStatusSubscriptions
{
    void SetStatusSubscriberCount(int count);
}

public sealed record PluginFeatureExecutionResult(bool Succeeded, string Message);

public interface IPluginRuntimeActionFeatures
{
    PluginFeatureExecutionResult ExecuteFeature(string featureId);
}

public interface IPluginRuntimePageFeatures
{
    void OpenPage(string featureId, IPluginPageSession session);
}
