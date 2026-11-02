using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

internal sealed class CSharpPluginPageBridge : IPluginPageApi, IDisposable
{
    private readonly object gate = new();
    private bool disposed;
    private readonly Dictionary<string, Action<WinPilot.PluginContract.IPluginPageSession>> handlers = new(StringComparer.Ordinal);

    public IDisposable Register(string featureId, Action<WinPilot.PluginContract.IPluginPageSession> opened)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentNullException.ThrowIfNull(opened);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!handlers.TryAdd(featureId, opened))
            {
                throw new InvalidOperationException($"页面功能已重复注册：{featureId}");
            }
        }
        return new Registration(() =>
        {
            lock (gate)
            {
                handlers.Remove(featureId);
            }
        });
    }

    public void Open(string featureId, IPluginPageSession session)
    {
        Action<WinPilot.PluginContract.IPluginPageSession> opened;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!handlers.TryGetValue(featureId, out opened!))
            {
                throw new InvalidOperationException("插件未注册该页面功能");
            }
        }
        opened(new CSharpPageSession(session));
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            handlers.Clear();
        }
    }

    private sealed class CSharpPageSession(IPluginPageSession session) : WinPilot.PluginContract.IPluginPageSession
    {
        public string Id => session.Id;

        public TResource Track<TResource>(TResource resource) where TResource : IDisposable
        {
            session.RegisterResource(resource);
            return resource;
        }

        public void HandleMessages(Func<string, CancellationToken, Task<string>> handler) =>
            session.RegisterMessageHandler(handler);

        public void HandleUploads(Func<WinPilot.PluginContract.PluginPageUpload, CancellationToken, Task<string>> handler) =>
            session.RegisterUploadHandler(handler);
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
