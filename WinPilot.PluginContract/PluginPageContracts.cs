namespace WinPilot.PluginContract;

/// <summary>
/// 一次功能页打开对应的会话，页面资源与消息处理应绑定此会话，而非插件全局生命周期。
/// </summary>
public interface IPluginPageSession
{
    string Id { get; }

    TResource Track<TResource>(TResource resource) where TResource : IDisposable;

    void HandleMessages(Func<string, CancellationToken, Task<string>> handler);

    void HandleUploads(Func<PluginPageUpload, CancellationToken, Task<string>> handler);
}

public sealed record PluginPageUpload(string Command, string ParametersJson, ReadOnlyMemory<byte> Content);

/// <summary>
/// 注册功能页打开回调；每次打开由宿主传入独立的页面会话。
/// </summary>
public interface IPluginPageApi
{
    IDisposable Register(string featureId, Action<IPluginPageSession> opened);
}
