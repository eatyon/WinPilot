using System.Reflection;
using System.Runtime.Loader;
using WinPilot.Core.Plugins;
using WinPilot.PluginContract;
using WinPilot.Features.Notifications.CommandBots.Commands;

namespace WinPilot.Features.Plugins;

public sealed class CSharpPluginRuntime(ILoggerFactory loggerFactory, IPluginHostSecurityApi security, PluginTaskExtensionRegistry? extensionRegistry = null, IPluginHostActionApi? hostActions = null, IPluginProcessApi? processes = null, PluginCommandRegistry? commandRegistry = null) : IPluginRuntime
{
    private readonly PluginTaskExtensionRegistry extensionRegistry = extensionRegistry ?? new(new(), new(), new());
    private readonly PluginCommandRegistry commandRegistry = commandRegistry ?? new(CommandProcessor.IsSupportedCommand);
    private readonly ILogger logger = loggerFactory.CreateLogger<CSharpPluginRuntime>();

    public IPluginRuntimeInstance Start(PluginInfo plugin)
    {
        var manifest = plugin.Manifest ?? throw new InvalidOperationException("插件 Manifest 不可用");
        var assemblyPath = ResolveAssemblyPath(plugin.Directory, manifest.Main);
        logger.LogDebug("开始加载 C# 插件，插件 ID：{PluginId}，入口文件：{Main}", plugin.Id, manifest.Main);
        var loadContext = new PluginAssemblyLoadContext(assemblyPath);
        try
        {
            using var assemblyStream = File.OpenRead(assemblyPath);
            var assembly = loadContext.LoadFromStream(assemblyStream);
            var pluginType = assembly.GetTypes().SingleOrDefault(type => typeof(ICSharpPlugin).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) is not null)
                ?? throw new InvalidOperationException("插件 DLL 未找到无参数的 ICSharpPlugin 实现");
            var logger = loggerFactory.CreateLogger($"Plugin:{manifest.Name}");
            var status = new PluginStatusBridge();
            var extensions = extensionRegistry.CreateScope(plugin.Id, manifest.Name);
            var actions = new CSharpPluginActionBridge();
            var pages = new CSharpPluginPageBridge();
            var commands = commandRegistry.CreateScope(plugin.Id, manifest.Name);
            var context = new CSharpPluginContext(plugin.Id, plugin.Directory, logger, new PluginStorageContext(plugin.DataDirectory), status, new PluginFeatureContext(actions, pages), new PluginTaskContext(extensions, extensions, extensions), new PluginHostContext(hostActions, processes, security))
            {
                Commands = commands
            };
            context.Track(commands);
            context.Track(extensions);
            context.Track(actions);
            context.Track(pages);
            try
            {
                ((ICSharpPlugin)Activator.CreateInstance(pluginType)!).Start(context);
                logger.LogDebug("C# 插件已加载，插件 ID：{PluginId}，实现类型：{PluginType}", plugin.Id, pluginType.FullName);
                return new Instance(plugin.Id, context, status, loadContext, actions, pages, logger);
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static string ResolveAssemblyPath(string pluginDirectory, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) || entry.Contains('/') || entry.Contains('\\')) throw new InvalidOperationException("C# 插件 main 必须是插件目录内的 DLL 文件名");
        var pluginRoot = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(pluginDirectory, entry));
        if (!path.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("C# 插件 main 必须指向插件目录内的 DLL");
        if (!File.Exists(path)) throw new FileNotFoundException("C# 插件 DLL 不存在", path);
        return path;
    }

    private sealed class Instance(string pluginId, CSharpPluginContext context, PluginStatusBridge status, PluginAssemblyLoadContext loadContext, CSharpPluginActionBridge actions, CSharpPluginPageBridge pages, ILogger logger) : IPluginRuntimeInstance, IPluginRuntimeStatus, IPluginRuntimeStatusSubscriptions, IPluginRuntimeActionFeatures, IPluginRuntimePageFeatures
    {
        private CSharpPluginContext? context = context;
        private PluginAssemblyLoadContext? loadContext = loadContext;
        public string? StatusJson => status.StatusJson;
        public event Action<string>? StateChanged { add => status.StateChanged += value; remove => status.StateChanged -= value; }
        public void SetStatusSubscriberCount(int count) => status.SetStatusSubscriberCount(count);
        public PluginFeatureExecutionResult ExecuteFeature(string featureId)
        {
            var result = actions.Execute(featureId);
            return new(result.Succeeded, result.Message);
        }
        public void OpenPage(string featureId, IPluginPageSession session) => pages.Open(featureId, session);
        public void Dispose()
        {
            DisposeResource(Interlocked.Exchange(ref context, null), "Context");
            try { Interlocked.Exchange(ref loadContext, null)?.Unload(); }
            catch (Exception exception) { logger.LogError(exception, "卸载 C# 插件程序集失败，插件 ID：{PluginId}，资源：{Resource}", pluginId, "LoadContext"); }
        }

        private void DisposeResource(IDisposable? resource, string resourceName)
        {
            try { resource?.Dispose(); }
            catch (Exception exception) { logger.LogError(exception, "释放 C# 插件资源失败，插件 ID：{PluginId}，资源：{Resource}", pluginId, resourceName); }
        }
    }

    private sealed class PluginAssemblyLoadContext(string mainAssemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(mainAssemblyPath);
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(ICSharpPlugin).Assembly.GetName().Name) return typeof(ICSharpPlugin).Assembly;
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

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
            if (!handlers.TryAdd(featureId, handler)) throw new InvalidOperationException($"点击执行功能已重复注册：{featureId}");
        }
        return new Registration(() => { lock (gate) handlers.Remove(featureId); });
    }

    public PluginFeatureActionResult Execute(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        PluginFeatureActionHandler handler;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!handlers.TryGetValue(featureId, out handler!)) throw new InvalidOperationException("插件未注册该点击执行功能");
        }
        var result = handler() ?? throw new InvalidOperationException("插件点击执行功能未返回结果");
        if (string.IsNullOrWhiteSpace(result.Message)) throw new InvalidOperationException("插件点击执行功能必须返回提示内容");
        return result;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
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
            if (!handlers.TryAdd(featureId, opened)) throw new InvalidOperationException($"页面功能已重复注册：{featureId}");
        }
        return new Registration(() => { lock (gate) handlers.Remove(featureId); });
    }

    public void Open(string featureId, IPluginPageSession session)
    {
        Action<WinPilot.PluginContract.IPluginPageSession> opened;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!handlers.TryGetValue(featureId, out opened!)) throw new InvalidOperationException("插件未注册该页面功能");
        }
        opened(new CSharpPageSession(session));
    }

    public void Dispose() { lock (gate) { disposed = true; handlers.Clear(); } }

    private sealed class CSharpPageSession(IPluginPageSession session) : WinPilot.PluginContract.IPluginPageSession
    {
        public string Id => session.Id;
        public TResource Track<TResource>(TResource resource) where TResource : IDisposable { session.RegisterResource(resource); return resource; }
        public void HandleMessages(Func<string, CancellationToken, Task<string>> handler) => session.RegisterMessageHandler(handler);
        public void HandleUploads(Func<WinPilot.PluginContract.PluginPageUpload, CancellationToken, Task<string>> handler) => session.RegisterUploadHandler(handler);
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}

public sealed class PluginStatusBridge : IPluginStatus, IDisposable
{
    private readonly object gate = new();
    private readonly PluginStatusDelivery delivery = new();
    private string? statusJson;
    private int subscriberCount;
    public string? StatusJson => Volatile.Read(ref statusJson);
    public event Action<string>? StateChanged;
    public event Action<int>? SubscriberCountChanged;

    public void Publish(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var normalized = document.RootElement.GetRawText();
        PluginStatusDelivery.Delivery pending;
        lock (gate)
        {
            if (string.Equals(statusJson, normalized, StringComparison.Ordinal)) return;
            Volatile.Write(ref statusJson, normalized);
            var callback = StateChanged;
            pending = delivery.Enqueue(_ => callback?.Invoke(normalized));
        }
        delivery.Deliver(pending);
    }

    public void SetStatusSubscriberCount(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (Interlocked.Exchange(ref subscriberCount, count) != count) SubscriberCountChanged?.Invoke(count);
    }

    public void Dispose() { StateChanged = null; SubscriberCountChanged = null; }
}
