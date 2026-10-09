using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WinPilot.PluginContract;

public interface ICSharpPlugin
{
    void Start(CSharpPluginContext context);
}

public sealed class CSharpPluginContext(string pluginId, string pluginDirectory, ILogger logger, PluginStorageContext storage, IPluginStatus status, PluginFeatureContext features, PluginTaskContext tasks, PluginHostContext host) : IDisposable
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

    public TResource Track<TResource>(TResource resource) where TResource : IDisposable
    {
        lock (gate)
        {
            if (disposed) { resource.Dispose(); throw new ObjectDisposedException(nameof(CSharpPluginContext)); }
            resources.Add(resource);
        }
        return resource;
    }

    public void Dispose()
    {
        IDisposable[] active;
        lock (gate) { if (disposed) return; disposed = true; active = resources.ToArray(); resources.Clear(); }
        foreach (var resource in active.Reverse())
        {
            try { resource.Dispose(); } catch (Exception exception) { Logger.LogWarning(exception, "插件 {PluginId} 资源释放失败", PluginId); }
        }
        Status.Dispose();
    }
}

public sealed class PluginStorageContext(string dataDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public string DataDirectory { get; } = dataDirectory;
    public string? ReadConfig() => ReadText("config.json");
    public T? ReadConfig<T>() => JsonSerializer.Deserialize<T>(ReadConfig() ?? "{}", JsonOptions);
    public T? ReadData<T>(string fileName) => JsonSerializer.Deserialize<T>(ReadText(fileName) ?? "null", JsonOptions);

    private string? ReadText(string fileName)
    {
        if (Path.IsPathRooted(fileName) || fileName.Contains('/') || fileName.Contains('\\') || fileName is "." or "..") throw new ArgumentException("插件数据文件必须是单级文件名", nameof(fileName));
        var path = Path.Combine(DataDirectory, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}

public sealed record PluginFeatureContext(IPluginFeatureActionApi? Actions, IPluginPageApi? Pages);
public sealed record PluginTaskContext(IPluginTaskTriggerApi? Triggers, IPluginNotificationVariableApi? Variables, IPluginTaskActionApi? Actions);
public sealed record PluginHostContext(IPluginHostActionApi? Actions, IPluginProcessApi? Processes, IPluginHostSecurityApi Security);

public interface IPluginStatus : IDisposable
{
    string? StatusJson { get; }
    event Action<string>? StateChanged;
    event Action<int>? SubscriberCountChanged;
    void Publish(string json);
}

public sealed record PluginTriggerDefinition(string TriggerId, string TriggerName, IReadOnlyList<PluginActionParameterDefinition>? Parameters = null);
public sealed record PluginTriggerEvent(string TriggerId, DateTimeOffset Timestamp, IReadOnlyDictionary<string, object?> Variables);
public enum PluginVariableType { Text, Image }
public sealed record PluginVariableDefinition(string Name, string DisplayName, string Description = "", PluginVariableType Type = PluginVariableType.Text);
public sealed record PluginImageContent(byte[] Content, string ContentType);
public enum PluginActionParameterType { Text, Number, Select, Checkbox, Textarea, MultiCheckbox }
public sealed record PluginActionOption(string Value, string Label);
public sealed record PluginActionParameterDefinition(string Key, string Label, PluginActionParameterType Type, bool Required = false, object? DefaultValue = null, IReadOnlyList<PluginActionOption>? Options = null);
public enum PluginHostActionType { StartProgram, Delay, Keyboard, Shell, SendNotification }
public sealed record PluginHostActionStep(PluginHostActionType Type, IReadOnlyDictionary<string, object?> Parameters);
public sealed record PluginHostActionPlan(IReadOnlyList<PluginHostActionStep> Steps);
public sealed record PluginTaskActionDefinition(string ActionId, string ActionName, IReadOnlyList<PluginActionParameterDefinition>? Parameters = null, PluginHostActionPlan? HostActionPlan = null);
public sealed record PluginTaskRunContext(string? TaskName, string? PluginId, string? TriggerId, DateTimeOffset TriggerTimestamp, IReadOnlyDictionary<string, object?> Variables, CancellationToken CancellationToken = default);
public delegate Task<IReadOnlyDictionary<string, object?>> PluginNotificationVariableResolver(IReadOnlyList<string> names, PluginTaskRunContext context, CancellationToken cancellationToken);
public sealed record PluginHostActionResult(bool Succeeded, string? Error = null);
public interface IPluginHostActionApi
{
    Task<PluginHostActionResult> ExecuteAsync(PluginHostActionStep step, CancellationToken cancellationToken = default);
}
public interface IPluginProcessApi
{
    bool IsRunning(string processName);
}
public interface IPluginHostSecurityApi
{
    bool SafeModeEnabled { get; }
}
public enum PluginTaskActionResultStatus { Success, Failed, Cancelled, Unavailable, InvalidParameters }
public sealed record PluginTaskActionResult(PluginTaskActionResultStatus Status, string? Error = null, PluginHostActionPlan? HostActionPlan = null) { public bool Succeeded => Status == PluginTaskActionResultStatus.Success; public static PluginTaskActionResult Success() => new(PluginTaskActionResultStatus.Success); public static PluginTaskActionResult Success(PluginHostActionPlan hostActionPlan) => new(PluginTaskActionResultStatus.Success, null, hostActionPlan); }
public delegate Task<PluginTaskActionResult> PluginTaskActionHandler(IReadOnlyDictionary<string, object?> parameters, PluginTaskRunContext context, CancellationToken cancellationToken);
public delegate Task<IReadOnlyList<PluginActionOption>> PluginActionOptionsResolver(string actionId, string parameterKey, CancellationToken cancellationToken);

public interface IPluginTaskTriggerApi
{
    IDisposable Register(IReadOnlyList<PluginTriggerDefinition> definitions);
    void Emit(PluginTriggerEvent trigger);
}

public interface IPluginNotificationVariableApi
{
    IDisposable Register(IReadOnlyList<PluginVariableDefinition> definitions);
    IDisposable Register(IReadOnlyList<PluginVariableDefinition> definitions, PluginNotificationVariableResolver resolver);
}

public interface IPluginTaskActionApi
{
    IDisposable Register(IReadOnlyList<PluginTaskActionDefinition> definitions);
    IDisposable Register(IReadOnlyList<PluginTaskActionDefinition> definitions, PluginTaskActionHandler handler);
    IDisposable Register(IReadOnlyList<PluginTaskActionDefinition> definitions, PluginTaskActionHandler handler, PluginActionOptionsResolver optionsResolver);
}

public interface IPluginPageSession
{
    string Id { get; }
    TResource Track<TResource>(TResource resource) where TResource : IDisposable;
    void HandleMessages(Func<string, CancellationToken, Task<string>> handler);
    void HandleUploads(Func<PluginPageUpload, CancellationToken, Task<string>> handler);
}

public sealed record PluginPageUpload(string Command, string ParametersJson, ReadOnlyMemory<byte> Content);

public sealed record PluginFeatureActionResult(bool Succeeded, string Message)
{
    public static PluginFeatureActionResult Success(string message) => new(true, message);
    public static PluginFeatureActionResult Failure(string message) => new(false, message);
}

public delegate PluginFeatureActionResult PluginFeatureActionHandler();

public interface IPluginFeatureActionApi
{
    IDisposable Register(string featureId, PluginFeatureActionHandler handler);
}

public interface IPluginPageApi
{
    IDisposable Register(string featureId, Action<IPluginPageSession> opened);
}
