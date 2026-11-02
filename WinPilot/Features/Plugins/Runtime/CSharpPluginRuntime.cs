using System.Reflection;
using System.Runtime.Loader;
using WinPilot.Core.Plugins;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

/// <summary>
/// 在可卸载的程序集上下文中启动 C# 插件，并把功能注册与资源归入本次运行。
/// </summary>
public sealed class CSharpPluginRuntime(
    ILoggerFactory loggerFactory,
    IPluginHostSecurityApi security,
    PluginTaskExtensionRegistry? extensionRegistry = null,
    IPluginHostActionApi? hostActions = null,
    IPluginProcessApi? processes = null,
    PluginCommandRegistry? commandRegistry = null) : IPluginRuntime
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
            // 从流加载入口 DLL，避免长期占用入口文件，便于插件停止后的文件替换。
            using var assemblyStream = File.OpenRead(assemblyPath);
            var assembly = loadContext.LoadFromStream(assemblyStream);
            var pluginType = assembly
                .GetTypes()
                .SingleOrDefault(type =>
                    typeof(ICSharpPlugin).IsAssignableFrom(type)
                    && !type.IsAbstract
                    && !type.IsInterface
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                ?? throw new InvalidOperationException("插件 DLL 未找到无参数的 ICSharpPlugin 实现");
            var logger = loggerFactory.CreateLogger($"Plugin:{manifest.Name}");
            var status = new PluginStatusBridge();
            var extensions = extensionRegistry.CreateScope(plugin.Id, manifest.Name);
            var actions = new CSharpPluginActionBridge();
            var pages = new CSharpPluginPageBridge();
            var commands = commandRegistry.CreateScope(plugin.Id, manifest.Name);
            var context = new CSharpPluginContext(
                plugin.Id,
                plugin.Directory,
                logger,
                new PluginStorageContext(plugin.DataDirectory),
                status,
                new PluginFeatureContext(actions, pages),
                new PluginTaskContext(extensions, extensions, extensions),
                new PluginHostContext(hostActions, processes, security))
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
        if (string.IsNullOrWhiteSpace(entry)
            || Path.IsPathRooted(entry)
            || entry.Contains('/')
            || entry.Contains('\\'))
        {
            throw new InvalidOperationException("C# 插件 main 必须是插件目录内的 DLL 文件名");
        }
        var pluginRoot = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(pluginDirectory, entry));
        if (!path.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("C# 插件 main 必须指向插件目录内的 DLL");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("C# 插件 DLL 不存在", path);
        }
        return path;
    }

    private sealed class Instance(
        string pluginId,
        CSharpPluginContext context,
        PluginStatusBridge status,
        PluginAssemblyLoadContext loadContext,
        CSharpPluginActionBridge actions,
        CSharpPluginPageBridge pages,
        ILogger logger) : IPluginRuntimeInstance, IPluginRuntimeStatus, IPluginRuntimeStatusSubscriptions, IPluginRuntimeActionFeatures, IPluginRuntimePageFeatures
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
            // 先撤销注册和释放插件资源，再请求卸载程序集上下文。
            DisposeResource(Interlocked.Exchange(ref context, null), "Context");
            try
            {
                Interlocked.Exchange(ref loadContext, null)?.Unload();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "卸载 C# 插件程序集失败，插件 ID：{PluginId}，资源：{Resource}", pluginId, "LoadContext");
            }
        }

        private void DisposeResource(IDisposable? resource, string resourceName)
        {
            try
            {
                resource?.Dispose();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "释放 C# 插件资源失败，插件 ID：{PluginId}，资源：{Resource}", pluginId, resourceName);
            }
        }
    }

    private sealed class PluginAssemblyLoadContext(string mainAssemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 契约必须复用宿主的程序集实例，否则同名接口在不同加载上下文中无法互相转换。
            if (assemblyName.Name == typeof(ICSharpPlugin).Assembly.GetName().Name)
            {
                return typeof(ICSharpPlugin).Assembly;
            }
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
