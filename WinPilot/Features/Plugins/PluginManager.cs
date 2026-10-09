using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using WinPilot.Core;
using WinPilot.Core.Plugins;

namespace WinPilot.Features.Plugins;

public sealed class PluginManager : IDisposable
{
    private const int MaxReadmeBytes = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions StorageJsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions LifecycleJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly PortableDataPaths paths;
    private readonly ILogger<PluginManager> logger;
    private readonly IPluginRuntime runtime;
    private readonly PluginPageSessionManager pageSessions;
    private readonly PluginConfigurationStore configurationStore = new();
    private readonly object gate = new();
    private readonly object lifecycleGate = new();
    private readonly HashSet<string> activeLifecycles = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource? lifecycleWaiters;
    private int pendingReloads;
    private bool reloading;
    private bool disposed;
    private IReadOnlyList<PluginInfo> plugins = [];
    private readonly Dictionary<string, IPluginRuntimeInstance> runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<string>>> stateSubscribers = new(StringComparer.OrdinalIgnoreCase);

    public PluginManager(PortableDataPaths paths, ILogger<PluginManager> logger, IPluginRuntime runtime, PluginPageSessionManager pageSessions)
    {
        this.paths = paths;
        this.logger = logger;
        this.runtime = runtime;
        this.pageSessions = pageSessions;
        Reload();
    }

    public IReadOnlyList<PluginInfo> GetPlugins()
    {
        lock (gate) return plugins.ToArray();
    }

    public PluginInfo GetPlugin(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (gate)
        {
            return plugins.FirstOrDefault(plugin => plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("插件不存在");
        }
    }

    public async Task<PluginInfo> GetPluginAfterLifecycleAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        while (true)
        {
            Task completion;
            lock (lifecycleGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!reloading && pendingReloads == 0 && !activeLifecycles.Contains(id)) return GetPlugin(id);
                lifecycleWaiters ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = lifecycleWaiters.Task;
            }
            await completion.WaitAsync(cancellationToken);
        }
    }

    public bool HasReadme(string id)
    {
        var path = Path.Combine(GetPlugin(id).Directory, "README.md");
        return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }

    public string ReadReadme(string id)
    {
        var path = Path.Combine(GetPlugin(id).Directory, "README.md");
        if (!HasReadme(id)) throw new FileNotFoundException("插件没有 README.md");
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxReadmeBytes) throw new InvalidOperationException("README.md 不能超过 512 KB");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }

    public void Enable(string id) => SetEnabled(id, true);
    public void Disable(string id) => SetEnabled(id, false);

    public PluginStatusDisplaySettings GetStatusDisplaySettings(string id)
    {
        var plugin = GetPlugin(id);
        var statusKeys = ReadStatusKeys(plugin.Id);
        var settings = ReadLifecycleState(plugin.DataDirectory)?.StatusDisplay;
        if (settings is null) return new(true, statusKeys, DefaultHomeTitle(plugin));
        return new(settings.RegisterOnHome, settings.StatusKeys?.Distinct(StringComparer.Ordinal).ToArray() ?? statusKeys, settings.HomeTitle);
    }

    public PluginStatusDisplaySettings WriteStatusDisplaySettings(string id, PluginStatusDisplaySettings settings)
        => RunLifecycle(id, () => WriteStatusDisplaySettingsCore(id, settings));

    private PluginStatusDisplaySettings WriteStatusDisplaySettingsCore(string id, PluginStatusDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var plugin = GetPlugin(id);
        var homeTitle = string.IsNullOrWhiteSpace(settings.HomeTitle) ? DefaultHomeTitle(plugin) : settings.HomeTitle.Trim();
        if (homeTitle.Length > 100) throw new InvalidOperationException("首页标题不能超过 100 个字符");
        var normalized = new PluginStatusDisplaySettings(settings.RegisterOnHome, settings.StatusKeys?.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray() ?? [], homeTitle);
        UpdateLifecycleState(plugin, lifecycle => lifecycle with { StatusDisplay = normalized });
        return normalized;
    }

    public PluginFeatureDisplaySettings GetFeatureDisplaySettings(string id)
    {
        var plugin = GetPlugin(id);
        var featureIds = ReadFeatureIds(plugin.Id);
        var settings = ReadLifecycleState(plugin.DataDirectory)?.FeatureDisplay;
        if (settings is null) return new(true, featureIds, DefaultFeatureHomeTitle(plugin));
        return new(settings.RegisterOnHome, settings.FeatureIds?.Distinct(StringComparer.Ordinal).ToArray() ?? featureIds, settings.HomeTitle);
    }

    public PluginFeatureDisplaySettings WriteFeatureDisplaySettings(string id, PluginFeatureDisplaySettings settings)
        => RunLifecycle(id, () => WriteFeatureDisplaySettingsCore(id, settings));

    private PluginFeatureDisplaySettings WriteFeatureDisplaySettingsCore(string id, PluginFeatureDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var plugin = GetPlugin(id);
        var homeTitle = string.IsNullOrWhiteSpace(settings.HomeTitle) ? DefaultFeatureHomeTitle(plugin) : settings.HomeTitle.Trim();
        if (homeTitle.Length > 100) throw new InvalidOperationException("首页标题不能超过 100 个字符");
        var availableIds = ReadFeatureIds(plugin.Id).ToHashSet(StringComparer.Ordinal);
        var featureIds = settings.FeatureIds?.Where(availableIds.Contains).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var normalized = new PluginFeatureDisplaySettings(settings.RegisterOnHome, featureIds, homeTitle);
        UpdateLifecycleState(plugin, lifecycle => lifecycle with { FeatureDisplay = normalized });
        return normalized;
    }

    public PluginSettingsDisplaySettings GetSettingsDisplaySettings(string id)
    {
        var plugin = GetPlugin(id);
        var settings = ReadLifecycleState(plugin.DataDirectory)?.SettingsDisplay;
        return settings is null ? new(true) : new(settings.RegisterOnHome);
    }

    public PluginSettingsDisplaySettings WriteSettingsDisplaySettings(string id, PluginSettingsDisplaySettings settings)
        => RunLifecycle(id, () => WriteSettingsDisplaySettingsCore(id, settings));

    private PluginSettingsDisplaySettings WriteSettingsDisplaySettingsCore(string id, PluginSettingsDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var plugin = GetPlugin(id);
        var normalized = new PluginSettingsDisplaySettings(settings.RegisterOnHome);
        UpdateLifecycleState(plugin, lifecycle => lifecycle with { SettingsDisplay = normalized });
        return normalized;
    }

    public void Delete(string id, bool deleteData)
    {
        RunLifecycle(id, () => DeleteCore(id, deleteData));
    }

    private void DeleteCore(string id, bool deleteData)
    {
        var plugin = GetPlugin(id);
        StopCore(plugin.Id);
        Directory.Delete(plugin.Directory, recursive: true);
        lock (gate)
        {
            plugins = plugins.Where(current => !current.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            stateSubscribers.Remove(plugin.Id);
        }
        if (deleteData && Directory.Exists(plugin.DataDirectory)) Directory.Delete(plugin.DataDirectory, recursive: true);
        logger.LogInformation("插件 {PluginId} 已删除，插件数据处理：{DeleteData}", plugin.Id, deleteData ? "删除" : "保留");
    }

    public string ReadStorage(string id)
    {
        var plugin = GetPlugin(id);
        var path = Path.Combine(plugin.DataDirectory, "config.json");
        return PluginSettingsSchema.ApplyDefaults(ReadSettingsDefinition(id),
            configurationStore.Read(path) ?? new JsonObject()).ToJsonString(StorageJsonOptions);
    }

    public void WriteStorage(string id, string content)
        => RunLifecycle(id, () => WriteStorageCore(id, content));

    public void WriteStorageAndRestart(string id, string content)
        => RunLifecycle(id, () => { WriteStorageCore(id, content); RestartCore(id); });

    private void WriteStorageCore(string id, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var plugin = GetPlugin(id);
        var definition = ReadSettingsDefinition(id);
        var submitted = JsonNode.Parse(content) as JsonObject
            ?? throw new InvalidOperationException("插件配置必须是 JSON 对象");
        var configuration = PluginSettingsSchema.ApplyDefaults(definition, submitted);
        PluginSettingsSchema.ValidateStorage(definition, configuration.ToJsonString());
        configurationStore.Update(Path.Combine(plugin.DataDirectory, "config.json"),
            _ => configuration);
    }

    public void Restart(string id)
    {
        RunLifecycle(id, () => RestartCore(id));
    }

    private void RestartCore(string id)
    {
        var plugin = GetPlugin(id);
        if (!plugin.Enabled || plugin.State == PluginState.Invalid) return;
        StopCore(plugin.Id);
        StartCore(plugin.Id);
    }

    public string? GetStatus(string id)
    {
        var plugin = GetPlugin(id);
        lock (gate)
        {
            return runtimes.TryGetValue(plugin.Id, out var runtime) && runtime is IPluginRuntimeStatus status
                ? status.StatusJson
                : null;
        }
    }

    private string[] ReadStatusKeys(string id)
    {
        var json = GetStatus(id);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
                : [];
        }
        catch (JsonException) { return []; }
    }

    private static string DefaultHomeTitle(PluginInfo plugin) => $"{plugin.Manifest?.Name ?? plugin.Id} 状态";
    private static string DefaultFeatureHomeTitle(PluginInfo plugin) => $"{plugin.Manifest?.Name ?? plugin.Id} 功能";

    private string[] ReadFeatureIds(string id)
    {
        var plugin = GetPlugin(id);
        if (string.IsNullOrWhiteSpace(plugin.Manifest?.FeaturesUi)) return [];
        using var document = JsonDocument.Parse(ReadFeaturesDefinition(id));
        return document.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString())
            .Where(featureId => !string.IsNullOrWhiteSpace(featureId))
            .Cast<string>()
            .ToArray();
    }

    public string ReadSettingsDefinition(string id)
    {
        var plugin = GetPlugin(id);
        if (string.IsNullOrWhiteSpace(plugin.Manifest?.SettingsUi)) return "[]";
        var path = ResolveDeclarationPath(plugin.Directory, plugin.Manifest.SettingsUi);
        var definition = File.ReadAllText(path);
        PluginSettingsSchema.ValidateDefinition(definition);
        return definition;
    }

    public string ReadFeaturesDefinition(string id)
    {
        var plugin = GetPlugin(id);
        if (string.IsNullOrWhiteSpace(plugin.Manifest?.FeaturesUi)) return "{\"features\":[]}";
        var path = ResolveDeclarationPath(plugin.Directory, plugin.Manifest.FeaturesUi);
        var definition = File.ReadAllText(path);
        PluginFeaturesSchema.ValidateDefinition(definition);
        return definition;
    }

    public PluginFeatureExecutionResult ExecuteFeature(string id, string featureId) => InvokeFeature(id, featureId);

    public string OpenPageFeature(string id, string featureId) => OpenPageFeatureSession(id, featureId).Id;

    public PluginPageFeatureSession OpenPageFeatureSession(string id, string featureId)
        => RunLifecycle(id, () => OpenPageFeatureSessionCore(id, featureId));

    private PluginPageFeatureSession OpenPageFeatureSessionCore(string id, string featureId)
    {
        var plugin = GetPlugin(id);
        if (!plugin.Enabled) throw new InvalidOperationException("插件未启用");
        using var document = JsonDocument.Parse(ReadFeaturesDefinition(id));
        var feature = document.RootElement.GetProperty("features").EnumerateArray().FirstOrDefault(item => string.Equals(item.GetProperty("id").GetString(), featureId, StringComparison.Ordinal));
        if (feature.ValueKind == JsonValueKind.Undefined || feature.GetProperty("mode").GetString() != "page") throw new InvalidOperationException("该 Feature 当前不支持功能页");
        IPluginRuntimePageFeatures pageFeatures;
        lock (gate)
        {
            if (!runtimes.TryGetValue(plugin.Id, out var instance) || instance is not IPluginRuntimePageFeatures features) throw new InvalidOperationException("插件未提供功能页能力");
            pageFeatures = features;
        }
        var sessionId = pageSessions.Open(plugin.Id, featureId, session => pageFeatures.OpenPage(featureId, session));
        return new PluginPageFeatureSession(sessionId, plugin.Manifest?.Name ?? plugin.Id, feature.Clone());
    }

    public sealed record PluginPageFeatureSession(string Id, string PluginName, JsonElement Feature);

    public void ClosePageSession(string sessionId) => pageSessions.Close(sessionId);
    public void TouchPageSession(string sessionId) => pageSessions.Touch(sessionId);
    public Task<string> SendPageMessageAsync(string sessionId, string message, CancellationToken cancellationToken) => pageSessions.SendAsync(sessionId, message, cancellationToken);
    public Task<string> UploadPageAsync(string sessionId, Func<CancellationToken, Task<WinPilot.PluginContract.PluginPageUpload>> readUpload, CancellationToken cancellationToken) => pageSessions.UploadAsync(sessionId, readUpload, cancellationToken);

    private PluginFeatureExecutionResult InvokeFeature(string id, string featureId)
        => RunLifecycle(id, () => InvokeFeatureCore(id, featureId));

    private PluginFeatureExecutionResult InvokeFeatureCore(string id, string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        var plugin = GetPlugin(id);
        using var document = JsonDocument.Parse(ReadFeaturesDefinition(id));
        var feature = document.RootElement.GetProperty("features").EnumerateArray().FirstOrDefault(item => string.Equals(item.GetProperty("id").GetString(), featureId, StringComparison.Ordinal));
        if (feature.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Feature 不存在");
        if (!string.Equals(feature.GetProperty("mode").GetString(), "action", StringComparison.Ordinal)) throw new InvalidOperationException("该 Feature 当前不支持执行");
        IPluginRuntimeInstance? instance;
        lock (gate) runtimes.TryGetValue(plugin.Id, out instance);
        if (instance is not IPluginRuntimeActionFeatures features) return new(false, "插件未提供点击执行功能能力");
        try
        {
            logger.LogDebug("开始执行插件功能，插件 ID：{PluginId}，功能 ID：{FeatureId}", plugin.Id, featureId);
            var result = features.ExecuteFeature(featureId);
            if (result.Succeeded) logger.LogInformation("插件功能执行完成，插件 ID：{PluginId}，功能 ID：{FeatureId}", plugin.Id, featureId);
            else logger.LogWarning("插件功能执行失败，插件 ID：{PluginId}，功能 ID：{FeatureId}", plugin.Id, featureId);
            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "执行插件功能时发生异常，插件 ID：{PluginId}，功能 ID：{FeatureId}", plugin.Id, featureId);
            throw;
        }
    }

    public IDisposable SubscribeState(string id, Action<string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var plugin = GetPlugin(id);
        int subscriberCount;
        lock (gate)
        {
            if (!stateSubscribers.TryGetValue(plugin.Id, out var subscribers)) stateSubscribers[plugin.Id] = subscribers = [];
            subscribers.Add(callback);
            subscriberCount = subscribers.Count;
        }
        try { NotifyStatusSubscriberCount(plugin.Id, subscriberCount); }
        catch
        {
            lock (gate)
            {
                if (stateSubscribers.TryGetValue(plugin.Id, out var subscribers))
                {
                    subscribers.Remove(callback);
                    if (subscribers.Count == 0) stateSubscribers.Remove(plugin.Id);
                }
            }
            TryNotifyStatusSubscriberCount(plugin.Id, GetStatusSubscriberCount(plugin.Id));
            throw;
        }
        return new StateSubscription(() =>
        {
            var remainingSubscribers = 0;
            lock (gate)
            {
                if (!stateSubscribers.TryGetValue(plugin.Id, out var subscribers)) return;
                subscribers.Remove(callback);
                remainingSubscribers = subscribers.Count;
                if (remainingSubscribers == 0) stateSubscribers.Remove(plugin.Id);
            }
            TryNotifyStatusSubscriberCount(plugin.Id, remainingSubscribers);
        });
    }

    private void NotifyStatusSubscriberCount(string id, int count)
    {
        IPluginRuntimeStatusSubscriptions? subscriptions;
        lock (gate) subscriptions = runtimes.TryGetValue(id, out var runtime) ? runtime as IPluginRuntimeStatusSubscriptions : null;
        subscriptions?.SetStatusSubscriberCount(count);
    }

    private void TryNotifyStatusSubscriberCount(string id, int count)
    {
        try { NotifyStatusSubscriberCount(id, count); }
        catch (Exception exception) { logger.LogWarning(exception, "插件 {PluginId} 更新状态订阅数量失败", id); }
    }

    private int GetStatusSubscriberCount(string id)
    {
        lock (gate) return stateSubscribers.TryGetValue(id, out var subscribers) ? subscribers.Count : 0;
    }

    private void PublishState(string id, string json)
    {
        Action<string>[] callbacks;
        lock (gate) callbacks = stateSubscribers.TryGetValue(id, out var subscribers) ? subscribers.ToArray() : [];
        foreach (var callback in callbacks)
        {
            try { callback(json); } catch (Exception exception) { logger.LogWarning(exception, "插件 {PluginId} 状态订阅回调失败", id); }
        }
    }

    private sealed class StateSubscription(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;
        public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
    }

    public void Load(string id)
    {
        RunLifecycle(id, () => LoadCore(id));
    }

    private void LoadCore(string id)
    {
        var plugin = GetPlugin(id);
        if (plugin.State == PluginState.Invalid) throw new InvalidOperationException("无效插件不能加载");
        if (!plugin.Enabled) throw new InvalidOperationException("已禁用插件不能加载");
        UpdatePlugin(plugin with { State = PluginState.Loaded, Errors = [] });
    }

    public void Start(string id)
    {
        RunLifecycle(id, () => StartCore(id));
    }

    private void StartCore(string id)
    {
        LoadCore(id);
        var plugin = GetPlugin(id);
        IPluginRuntimeInstance? instance = null;
        try
        {
            pageSessions.CloseByPlugin(plugin.Id);
            IPluginRuntimeInstance? previous;
            lock (gate)
            {
                runtimes.Remove(plugin.Id, out previous);
            }
            DisposeRuntime(previous, plugin.Id);
            instance = runtime.Start(plugin);
            if (instance is IPluginRuntimeStatus status) status.StateChanged += json => PublishState(plugin.Id, json);
            lock (gate) runtimes[plugin.Id] = instance;
            NotifyStatusSubscriberCount(plugin.Id, GetStatusSubscriberCount(plugin.Id));
            UpdatePlugin(plugin with { State = PluginState.Running, Errors = [] });
        }
        catch (Exception exception)
        {
            lock (gate)
                if (runtimes.TryGetValue(plugin.Id, out var current) && ReferenceEquals(current, instance))
                    runtimes.Remove(plugin.Id);
            DisposeRuntime(instance, plugin.Id);
            logger.LogError(exception, "插件 {PluginId} 启动失败", plugin.Id);
            UpdatePlugin(plugin with { State = PluginState.Failed, Errors = [$"插件执行失败：{exception.Message}"] });
        }
    }

    public void Stop(string id)
    {
        RunLifecycle(id, () => StopCore(id));
    }

    private void StopCore(string id)
    {
        var plugin = GetPlugin(id);
        pageSessions.CloseByPlugin(plugin.Id);
        IPluginRuntimeInstance? instance;
        lock (gate) runtimes.Remove(plugin.Id, out instance);
        DisposeRuntime(instance, plugin.Id);
        if (plugin.State is PluginState.Loaded or PluginState.Running or PluginState.Failed)
        {
            UpdatePlugin(plugin with { State = plugin.Enabled ? PluginState.Stopped : PluginState.Disabled });
        }
    }

    public void Reload()
    {
        lock (lifecycleGate)
        {
            pendingReloads++;
            try
            {
                while (reloading || activeLifecycles.Count > 0)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    Monitor.Wait(lifecycleGate);
                }
                ObjectDisposedException.ThrowIf(disposed, this);
                reloading = true;
            }
            finally
            {
                pendingReloads--;
                NotifyLifecycleWaiters();
            }
        }
        try { ReloadCore(); }
        finally
        {
            lock (lifecycleGate)
            {
                reloading = false;
                NotifyLifecycleWaiters();
            }
        }
    }

    private void ReloadCore()
    {
        try
        {
            StopAll();
            paths.EnsurePluginsDirectory();
            var discovered = Directory.EnumerateDirectories(paths.PluginsDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(directory => Discover(directory))
                .Where(plugin => plugin is not null)
                .Select(plugin => plugin!)
                .OrderBy(plugin => plugin.Manifest?.Name ?? plugin.DirectoryName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            var duplicateIds = discovered.Where(plugin => plugin.Manifest is not null)
                .GroupBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (duplicateIds.Count > 0)
            {
                foreach (var duplicateId in duplicateIds) logger.LogWarning("忽略重复插件 ID {PluginId}", duplicateId);
                discovered = discovered.Where(plugin => !duplicateIds.Contains(plugin.Id)).ToArray();
            }

            lock (gate) plugins = discovered;
            logger.LogInformation("插件扫描完成，发现 {Count} 个插件目录", discovered.Length);
            foreach (var plugin in discovered.Where(plugin => plugin.Enabled && plugin.State != PluginState.Invalid)) StartCore(plugin.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "扫描插件目录失败");
            lock (gate) plugins = [];
        }
    }

    private PluginInfo? Discover(string directory)
    {
        try { return DiscoverCore(directory); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "跳过无法读取的插件目录 {Directory}", directory);
            return null;
        }
    }

    private PluginInfo? DiscoverCore(string directory)
    {
        var directoryName = Path.GetFileName(directory);
        var errors = new List<string>();
        PluginManifest? manifest = null;
        var manifestFile = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestFile))
        {
            errors.Add("缺少 manifest.json");
        }
        else
        {
            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestFile), JsonOptions);
                if (manifest is null) errors.Add("manifest.json 不能为空");
            }
            catch (JsonException exception)
            {
                errors.Add($"manifest.json 不是有效 JSON：{exception.Message}");
            }
            catch (Exception exception)
            {
                errors.Add($"无法读取 manifest.json：{exception.Message}");
            }
        }

        if (manifest is not null) ValidateManifest(manifest, directory, errors);
        if (manifest is null)
        {
            logger.LogWarning("忽略无效插件目录 {Directory}：{Errors}", directory, string.Join("；", errors));
            return null;
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) || !IsValidId(manifest.Id))
        {
            logger.LogWarning("跳过无效插件目录 {Directory}：{Errors}", directory, string.Join("；", errors));
            return null;
        }
        var id = manifest.Id.Trim();
        var enabled = ReadLifecycleEnabled(id, Path.Combine(paths.PluginDataDirectory, id));
        var versionIncompatible = errors.Count == 1 && IsVersionIncompatible(manifest);
        if (errors.Count > 0 && !versionIncompatible)
        {
            logger.LogWarning("忽略无效插件目录 {Directory}：{Errors}", directory, string.Join("；", errors));
            return null;
        }
        if (errors.Count > 0) logger.LogWarning("发现无效插件目录 {Directory}：{Errors}", directory, string.Join("；", errors));
        var canChangeEnabled = errors.Count == 0 || versionIncompatible;
        if (errors.Count == 0)
        {
            try
            {
                var dataDirectory = Path.Combine(paths.PluginDataDirectory, id);
                configurationStore.Initialize(Path.Combine(dataDirectory, "plugin.json"),
                    () => JsonSerializer.SerializeToNode(new PluginLifecycleState(), LifecycleJsonOptions)!.AsObject());
                if (!string.IsNullOrWhiteSpace(manifest.SettingsUi))
                    configurationStore.Initialize(Path.Combine(dataDirectory, "config.json"),
                        () =>
                        {
                            var definition = File.ReadAllText(ResolveDeclarationPath(directory, manifest.SettingsUi));
                            PluginSettingsSchema.ValidateDefinition(definition);
                            return PluginSettingsSchema.ApplyDefaults(definition, new JsonObject());
                        });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                logger.LogWarning(exception, "插件 {PluginId} 配置初始化失败", id);
                errors.Add("插件配置初始化失败，请检查数据目录权限和配置声明");
                canChangeEnabled = false;
            }
        }
        var state = errors.Count > 0 ? PluginState.Invalid : enabled ? PluginState.Discovered : PluginState.Disabled;
        return new PluginInfo(id, directoryName, directory, Path.Combine(paths.PluginDataDirectory, id), manifest, state, enabled, errors, canChangeEnabled);
    }

    private static bool IsVersionIncompatible(PluginManifest manifest) =>
        !string.IsNullOrWhiteSpace(manifest.WinPilotVersion)
        && Version.TryParse(manifest.WinPilotVersion, out var minimumVersion)
        && minimumVersion > WinPilotVersion.Version;

    private static void ValidateManifest(PluginManifest manifest, string directory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || !IsValidId(manifest.Id)) errors.Add("id 不存在或格式无效，只能使用小写字母、数字和连字符");
        if (string.IsNullOrWhiteSpace(manifest.Name)) errors.Add("name 不能为空");
        if (string.IsNullOrWhiteSpace(manifest.Version)) errors.Add("version 不能为空");
        if (string.IsNullOrWhiteSpace(manifest.Main)) errors.Add("main 不能为空");
        else if (Path.IsPathRooted(manifest.Main) || manifest.Main.Contains('/') || manifest.Main.Contains('\\') || !manifest.Main.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(directory, manifest.Main))) errors.Add("main 必须是插件目录内存在的 DLL 文件");
        if (!string.IsNullOrWhiteSpace(manifest.WinPilotVersion) && (!Version.TryParse(manifest.WinPilotVersion, out var minimumVersion) || minimumVersion > WinPilotVersion.Version)) errors.Add($"需要 WinPilot {manifest.WinPilotVersion} 或更高版本");
        foreach (var author in manifest.Authors ?? [])
        {
            if (!string.IsNullOrWhiteSpace(author.Links) && (!Uri.TryCreate(author.Links, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))) errors.Add("authors.links 必须是 HTTP 或 HTTPS 地址");
        }
        ValidateUiDeclaration(manifest.SettingsUi, "settings_ui", directory, errors, PluginSettingsSchema.ValidateDefinition);
        ValidateUiDeclaration(manifest.FeaturesUi, "features_ui", directory, errors, PluginFeaturesSchema.ValidateDefinition);
        if (!string.IsNullOrWhiteSpace(manifest.FeaturesUi))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(ResolveDeclarationPath(directory, manifest.FeaturesUi)));
                foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
                {
                    if (feature.GetProperty("mode").GetString() == "page")
                    {
                        var entry = feature.GetProperty("entry").GetString();
                        if (string.IsNullOrWhiteSpace(entry) || !IsWebEntryInsidePlugin(directory, Path.Combine("web", entry)) || !File.Exists(Path.Combine(directory, "web", entry)))
                            errors.Add($"功能页 Feature {feature.GetProperty("id").GetString()} 的 entry 不存在或不在插件 web 目录内");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException) { }
        }
    }

    private static void ValidateUiDeclaration(string? fileName, string fieldName, string directory, List<string> errors, Action<string> validate)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        try
        {
            var path = ResolveDeclarationPath(directory, fileName);
            if (!File.Exists(path)) { errors.Add($"{fieldName} 指定的文件不存在"); return; }
            validate(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            errors.Add($"{fieldName} 无效：{exception.Message}");
        }
    }

    private static string ResolveDeclarationPath(string directory, string fileName)
    {
        if (Path.IsPathRooted(fileName) || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("声明文件必须是插件目录内的 JSON 文件");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("声明文件路径超出插件目录");
        return path;
    }

    private T RunLifecycle<T>(string id, Func<T> action)
    {
        T result = default!;
        RunLifecycle(id, () => { result = action(); });
        return result;
    }

    private void RunLifecycle(string id, Action action)
    {
        lock (lifecycleGate)
        {
            while (reloading || pendingReloads > 0 || activeLifecycles.Contains(id))
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Monitor.Wait(lifecycleGate);
            }
            ObjectDisposedException.ThrowIf(disposed, this);
            activeLifecycles.Add(id);
        }
        try { action(); }
        finally
        {
            lock (lifecycleGate)
            {
                activeLifecycles.Remove(id);
                NotifyLifecycleWaiters();
            }
        }
    }

    private void NotifyLifecycleWaiters()
    {
        Monitor.PulseAll(lifecycleGate);
        var waiters = lifecycleWaiters;
        lifecycleWaiters = null;
        waiters?.TrySetResult();
    }

    private void SetEnabled(string id, bool enabled)
    {
        RunLifecycle(id, () => SetEnabledCore(id, enabled));
    }

    private void SetEnabledCore(string id, bool enabled)
    {
        var plugin = GetPlugin(id);
        if (plugin.State == PluginState.Invalid)
        {
            if (!plugin.CanChangeEnabled) throw new InvalidOperationException("无效插件不能修改启用状态");
            WriteLifecycleEnabled(plugin, enabled);
            UpdatePlugin(plugin with { Enabled = enabled });
            logger.LogInformation("插件 {PluginId} 的启用设置已{Action}，因插件不兼容，仍不会运行", plugin.Id, enabled ? "开启" : "关闭");
            return;
        }
        if (!enabled) StopCore(id);
        WriteLifecycleEnabled(plugin, enabled);
        if (enabled)
        {
            UpdatePlugin(plugin with { Enabled = true, State = PluginState.Discovered, Errors = [] });
            StartCore(plugin.Id);
        }
        else
        {
            UpdatePlugin(plugin with { Enabled = false, State = PluginState.Disabled });
        }
        logger.LogInformation("插件 {PluginId} 已{Action}", plugin.Id, enabled ? "启用" : "禁用");
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
            NotifyLifecycleWaiters();
            while (reloading || activeLifecycles.Count > 0) Monitor.Wait(lifecycleGate);
        }
        StopAll();
    }

    private void StopAll()
    {
        pageSessions.CloseAll();
        KeyValuePair<string, IPluginRuntimeInstance>[] active;
        lock (gate)
        {
            active = runtimes.ToArray();
            runtimes.Clear();
        }
        foreach (var instance in active) DisposeRuntime(instance.Value, instance.Key);
    }

    private void DisposeRuntime(IPluginRuntimeInstance? instance, string pluginId)
    {
        if (instance is null) return;
        try { instance.Dispose(); }
        catch (Exception exception) { logger.LogError(exception, "停止插件 {PluginId} 并释放资源失败", pluginId); }
    }

    private void UpdatePlugin(PluginInfo plugin)
    {
        lock (gate)
        {
            plugins = plugins.Select(current => current.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase) ? plugin : current).ToArray();
        }
    }

    private bool ReadLifecycleEnabled(string id, string dataDirectory)
    {
        try
        {
            return ReadLifecycleState(dataDirectory)?.Enabled == true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "无法读取插件 {PluginId} 的启用设置，按禁用处理", id);
            return false;
        }
    }

    private PluginLifecycleState? ReadLifecycleState(string dataDirectory) =>
        configurationStore.Read(Path.Combine(dataDirectory, "plugin.json"))?.Deserialize<PluginLifecycleState>(JsonOptions);

    private void WriteLifecycleEnabled(PluginInfo plugin, bool enabled)
    {
        UpdateLifecycleState(plugin, lifecycle => lifecycle with { Enabled = enabled });
    }

    private void UpdateLifecycleState(PluginInfo plugin, Func<PluginLifecycleState, PluginLifecycleState> update)
    {
        var path = Path.Combine(plugin.DataDirectory, "plugin.json");
        configurationStore.Update(path, current =>
        {
            var state = current.Deserialize<PluginLifecycleState>(JsonOptions) ?? new PluginLifecycleState();
            var next = JsonSerializer.SerializeToNode(update(state), LifecycleJsonOptions)!.AsObject();
            foreach (var property in current)
                if (!next.Any(item => item.Key.Equals(property.Key, StringComparison.OrdinalIgnoreCase)))
                    next.Add(property.Key, property.Value?.DeepClone());
            return next;
        });
    }

    private sealed record PluginLifecycleState(bool Enabled = false, PluginStatusDisplaySettings? StatusDisplay = null, PluginFeatureDisplaySettings? FeatureDisplay = null, PluginSettingsDisplaySettings? SettingsDisplay = null);
    public sealed record PluginStatusDisplaySettings(bool RegisterOnHome = true, IReadOnlyList<string>? StatusKeys = null, string HomeTitle = "");
    public sealed record PluginFeatureDisplaySettings(bool RegisterOnHome = true, IReadOnlyList<string>? FeatureIds = null, string HomeTitle = "");
    public sealed record PluginSettingsDisplaySettings(bool RegisterOnHome = true);
    private static bool IsValidId(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9]+(?:-[a-z0-9]+)*$");
    private static bool IsWebEntryInsidePlugin(string directory, string entry)
    {
        if (Path.IsPathRooted(entry)) return false;
        var webRoot = Path.GetFullPath(Path.Combine(directory, "web")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullEntry = Path.GetFullPath(Path.Combine(directory, entry));
        return fullEntry.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase);
    }
}
