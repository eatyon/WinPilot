namespace WinPilot.Features.Plugins;

public interface IPluginPageSession
{
    string Id { get; }
    void RegisterResource(IDisposable resource);
    void RegisterMessageHandler(Func<string, CancellationToken, Task<string>> handler);
    void RegisterUploadHandler(Func<WinPilot.PluginContract.PluginPageUpload, CancellationToken, Task<string>> handler);
}

public sealed class PluginPageSessionManager(ILogger<PluginPageSessionManager> logger) : IDisposable
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private System.Threading.Timer? cleanupTimer;
    private bool disposed;

    public string Open(string pluginId, string featureId, Action<IPluginPageSession> opened)
    {
        Session session;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            session = new Session(Guid.NewGuid().ToString("N"), pluginId, gate, logger);
            sessions.Add(session.Id, session);
            cleanupTimer ??= new System.Threading.Timer(_ => CloseExpired(), null, CleanupInterval, CleanupInterval);
        }
        try
        {
            opened(session);
            return session.Id;
        }
        catch (OperationCanceledException)
        {
            Close(session.Id);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "打开插件功能页面失败，插件 ID：{PluginId}，功能 ID：{FeatureId}，会话 ID：{SessionId}", pluginId, featureId, session.Id);
            Close(session.Id);
            throw new PluginOperationException("插件功能页打开失败，请检查程序日志", exception, logged: true);
        }
    }

    public void Close(string id)
    {
        Session? session;
        lock (gate)
        {
            sessions.Remove(id, out session);
            StopCleanupIfIdle();
        }
        session?.Dispose();
    }

    public Task<string> SendAsync(string id, string message, CancellationToken cancellationToken)
    {
        Session session;
        lock (gate) session = sessions.TryGetValue(id, out var current) ? current : throw new KeyNotFoundException("插件页面会话不存在");
        return session.SendAsync(message, cancellationToken);
    }

    public void Touch(string id)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var session)) throw new KeyNotFoundException("插件页面会话不存在");
            session.Touch();
        }
    }

    public Task<string> UploadAsync(string id, Func<CancellationToken, Task<WinPilot.PluginContract.PluginPageUpload>> readUpload, CancellationToken cancellationToken)
    {
        Session session;
        lock (gate) session = sessions.TryGetValue(id, out var current) ? current : throw new KeyNotFoundException("插件页面会话不存在");
        return session.UploadAsync(readUpload, cancellationToken);
    }

    public void CloseByPlugin(string pluginId) =>
        CloseSessions(session => session.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase));

    public void CloseAll() => CloseSessions(_ => true);

    public void Dispose()
    {
        lock (gate) disposed = true;
        CloseAll();
    }

    private void CloseExpired() => CloseSessions(session => !disposed && session.IsExpired(SessionTimeout));

    private void CloseSessions(Func<Session, bool> predicate)
    {
        Session[] closing;
        lock (gate)
        {
            closing = sessions.Values.Where(predicate).ToArray();
            foreach (var session in closing) sessions.Remove(session.Id);
            StopCleanupIfIdle();
        }
        foreach (var session in closing) session.Dispose();
    }

    private void StopCleanupIfIdle()
    {
        if (sessions.Count != 0) return;
        cleanupTimer?.Dispose();
        cleanupTimer = null;
    }

    private sealed class Session(string id, string pluginId, object gate, ILogger logger) : IPluginPageSession
    {
        private readonly List<IDisposable> resources = [];
        private readonly CancellationTokenSource lifetime = new();
        private Func<string, CancellationToken, Task<string>>? messageHandler;
        private Func<WinPilot.PluginContract.PluginPageUpload, CancellationToken, Task<string>>? uploadHandler;
        private long lastActivity = Environment.TickCount64;
        private bool closed;
        private int activeRequests;
        private bool cancellationCompleted;
        private bool resourcesDisposed;
        public string Id => id;
        public string PluginId => pluginId;
        public void RegisterResource(IDisposable resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            lock (gate)
            {
                if (!closed) { resources.Add(resource); return; }
            }
            resource.Dispose();
        }
        public void RegisterMessageHandler(Func<string, CancellationToken, Task<string>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (gate)
            {
                if (closed) throw new ObjectDisposedException(nameof(IPluginPageSession));
                if (messageHandler is not null) throw new InvalidOperationException("插件页面消息处理器已注册");
                messageHandler = handler;
            }
        }
        public async Task<string> SendAsync(string message, CancellationToken cancellationToken)
        {
            Func<string, CancellationToken, Task<string>> handler;
            CancellationTokenSource linked;
            lock (gate)
            {
                if (closed) throw new KeyNotFoundException("插件页面会话不存在");
                handler = messageHandler ?? throw new PluginOperationException("插件页面消息能力不可用，请检查程序日志", new InvalidOperationException("插件页面未注册消息处理器"));
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                activeRequests++;
                Touch();
            }
            try
            {
                using (linked)
                {
                    try { return await handler(message, linked.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "处理插件功能页面消息失败，插件 ID：{PluginId}，会话 ID：{SessionId}", PluginId, Id);
                        throw new PluginOperationException("插件页面消息处理失败，请检查程序日志", exception, logged: true);
                    }
                }
            }
            finally { CompleteRequest(); }
        }
        public void RegisterUploadHandler(Func<WinPilot.PluginContract.PluginPageUpload, CancellationToken, Task<string>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (gate)
            {
                if (closed) throw new ObjectDisposedException(nameof(IPluginPageSession));
                if (uploadHandler is not null) throw new InvalidOperationException("插件页面上传处理器已注册");
                uploadHandler = handler;
            }
        }
        public async Task<string> UploadAsync(Func<CancellationToken, Task<WinPilot.PluginContract.PluginPageUpload>> readUpload, CancellationToken cancellationToken)
        {
            Func<WinPilot.PluginContract.PluginPageUpload, CancellationToken, Task<string>> handler;
            CancellationTokenSource linked;
            lock (gate)
            {
                if (closed) throw new KeyNotFoundException("插件页面会话不存在");
                handler = uploadHandler ?? throw new PluginOperationException("插件页面上传能力不可用，请检查程序日志", new InvalidOperationException("插件页面未注册上传处理器"));
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                activeRequests++;
                Touch();
            }
            try
            {
                using (linked)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    WinPilot.PluginContract.PluginPageUpload upload;
                    try { upload = await readUpload(linked.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (BadHttpRequestException) { throw; }
                    catch (Exception exception) { throw new PluginOperationException("无法读取插件上传数据，请检查程序日志", exception); }
                    linked.Token.ThrowIfCancellationRequested();
                    try { return await handler(upload, linked.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "处理插件功能页面上传失败，插件 ID：{PluginId}，会话 ID：{SessionId}", PluginId, Id);
                        throw new PluginOperationException("插件页面上传处理失败，请检查程序日志", exception, logged: true);
                    }
                }
            }
            finally { CompleteRequest(); }
        }
        public void Touch() => Volatile.Write(ref lastActivity, Environment.TickCount64);
        public bool IsExpired(TimeSpan timeout) => activeRequests == 0 && Environment.TickCount64 - Volatile.Read(ref lastActivity) >= timeout.TotalMilliseconds;
        private void CompleteRequest()
        {
            lock (gate) { activeRequests--; if (!closed) Touch(); }
            ReleaseResourcesIfIdle();
        }
        public void Dispose()
        {
            lock (gate) { if (closed) return; closed = true; messageHandler = null; uploadHandler = null; }
            try { lifetime.Cancel(); }
            catch (AggregateException exception)
            {
                logger.LogWarning(exception, "取消插件功能页面请求失败，插件 ID：{PluginId}，会话 ID：{SessionId}", PluginId, Id);
            }
            finally
            {
                lock (gate) cancellationCompleted = true;
                ReleaseResourcesIfIdle();
            }
        }
        private void ReleaseResourcesIfIdle()
        {
            IDisposable[] current;
            lock (gate)
            {
                if (!closed || !cancellationCompleted || activeRequests != 0 || resourcesDisposed) return;
                resourcesDisposed = true;
                current = resources.ToArray();
                resources.Clear();
            }
            try
            {
                foreach (var resource in current)
                {
                    try { resource.Dispose(); }
                    catch (Exception exception) { logger.LogWarning(exception, "清理插件功能页面资源失败，插件 ID：{PluginId}，会话 ID：{SessionId}", PluginId, Id); }
                }
            }
            finally { lifetime.Dispose(); }
        }
    }
}
