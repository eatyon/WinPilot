namespace WinPilot.Features.Plugins;

public interface IPluginPageSession
{
    string Id { get; }

    void RegisterResource(IDisposable resource);

    void RegisterMessageHandler(Func<string, CancellationToken, Task<string>> handler);

    void RegisterUploadHandler(Func<WinPilot.PluginContract.PluginPageUpload, CancellationToken, Task<string>> handler);
}
