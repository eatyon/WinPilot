namespace WinPilot.PluginContract;

public enum PluginActionParameterType
{
    Text,
    Number,
    Select,
    Checkbox,
    Textarea,
    MultiCheckbox
}

public sealed record PluginActionOption(string Value, string Label);

public sealed record PluginActionParameterDefinition(
    string Key,
    string Label,
    PluginActionParameterType Type,
    bool Required = false,
    object? DefaultValue = null,
    IReadOnlyList<PluginActionOption>? Options = null);

public sealed record PluginTaskActionDefinition(
    string ActionId,
    string ActionName,
    IReadOnlyList<PluginActionParameterDefinition>? Parameters = null,
    PluginHostActionPlan? HostActionPlan = null);

/// <summary>
/// 单次任务执行上下文，携带触发来源、触发时间、可用变量和本次执行的取消令牌。
/// </summary>
public sealed record PluginTaskRunContext(
    string? TaskName,
    string? PluginId,
    string? TriggerId,
    DateTimeOffset TriggerTimestamp,
    IReadOnlyDictionary<string, object?> Variables,
    CancellationToken CancellationToken = default);

public enum PluginTaskActionResultStatus
{
    Success,
    Failed,
    Cancelled,
    Unavailable,
    InvalidParameters
}

public sealed record PluginTaskActionResult(
    PluginTaskActionResultStatus Status,
    string? Error = null,
    PluginHostActionPlan? HostActionPlan = null)
{
    public bool Succeeded => Status == PluginTaskActionResultStatus.Success;

    public static PluginTaskActionResult Success() => new(PluginTaskActionResultStatus.Success);

    public static PluginTaskActionResult Success(PluginHostActionPlan hostActionPlan) =>
        new(PluginTaskActionResultStatus.Success, null, hostActionPlan);
}

public delegate Task<PluginTaskActionResult> PluginTaskActionHandler(
    IReadOnlyDictionary<string, object?> parameters,
    PluginTaskRunContext context,
    CancellationToken cancellationToken);

public delegate Task<IReadOnlyList<PluginActionOption>> PluginActionOptionsResolver(
    string actionId,
    string parameterKey,
    CancellationToken cancellationToken);

/// <summary>
/// 注册任务动作，可提供宿主动作计划、执行处理器及动态参数选项。
/// </summary>
public interface IPluginTaskActionApi
{
    IDisposable Register(IReadOnlyList<PluginTaskActionDefinition> definitions);

    IDisposable Register(IReadOnlyList<PluginTaskActionDefinition> definitions, PluginTaskActionHandler handler);

    IDisposable Register(
        IReadOnlyList<PluginTaskActionDefinition> definitions,
        PluginTaskActionHandler handler,
        PluginActionOptionsResolver optionsResolver);
}
