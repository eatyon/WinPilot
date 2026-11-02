using Microsoft.Extensions.Logging;

namespace WinPilot.PluginContract;

/// <summary>
/// 单次插件运行的宿主上下文，提供存储、状态与功能接口，并持有该次运行登记的资源。
/// </summary>
public sealed class CSharpPluginContext(
    string pluginId,
    string pluginDirectory,
    ILogger logger,
    PluginStorageContext storage,
    IPluginStatus status,
    PluginFeatureContext features,
    PluginTaskContext tasks,
    PluginHostContext host) : IDisposable
{
    private readonly object gate = new();
    private readonly List<IDisposable> resources = [];
    private bool disposed;

    public string PluginId { get; } = pluginId;
    public string PluginDirectory { get; } = pluginDirectory;
    public ILogger Logger { get; } = logger;
    public PluginStorageContext Storage { get; } = storage;
    public IPluginStatus Status { get; } = status;
    public PluginFeatureContext Features { get; } = features;
    public PluginTaskContext Tasks { get; } = tasks;
    public PluginHostContext Host { get; } = host;
    public IPluginCommandApi? Commands { get; init; }

    /// <summary>
    /// 将资源交给上下文管理，卸载时按登记的逆序释放；已卸载时立即释放传入资源并抛出异常。
    /// </summary>
    public TResource Track<TResource>(TResource resource) where TResource : IDisposable
    {
        lock (gate)
        {
            if (disposed)
            {
                resource.Dispose();
                throw new ObjectDisposedException(nameof(CSharpPluginContext));
            }
            resources.Add(resource);
        }
        return resource;
    }

    public void Dispose()
    {
        IDisposable[] active;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            active = resources.ToArray();
            resources.Clear();
        }
        foreach (var resource in active.Reverse())
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                Logger.LogWarning(exception, "插件 {PluginId} 资源释放失败", PluginId);
            }
        }
        Status.Dispose();
    }
}

public sealed record PluginFeatureContext(IPluginFeatureActionApi? Actions, IPluginPageApi? Pages);

public sealed record PluginTaskContext(
    IPluginTaskTriggerApi? Triggers,
    IPluginNotificationVariableApi? Variables,
    IPluginTaskActionApi? Actions);

public sealed record PluginHostContext(
    IPluginHostActionApi? Actions,
    IPluginProcessApi? Processes,
    IPluginHostSecurityApi Security);
