using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

internal sealed class CSharpPluginActionBridge : IPluginFeatureActionApi, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, PluginFeatureActionHandler> handlers = new(StringComparer.Ordinal);
    private bool disposed;

    public IDisposable Register(string featureId, PluginFeatureActionHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!handlers.TryAdd(featureId, handler))
            {
                throw new InvalidOperationException($"点击执行功能已重复注册：{featureId}");
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

    public PluginFeatureActionResult Execute(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        PluginFeatureActionHandler handler;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!handlers.TryGetValue(featureId, out handler!))
            {
                throw new InvalidOperationException("插件未注册该点击执行功能");
            }
        }
        var result = handler() ?? throw new InvalidOperationException("插件点击执行功能未返回结果");
        if (string.IsNullOrWhiteSpace(result.Message))
        {
            throw new InvalidOperationException("插件点击执行功能必须返回提示内容");
        }
        return result;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            handlers.Clear();
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
