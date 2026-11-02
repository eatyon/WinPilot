namespace WinPilot.PluginContract;

public sealed record PluginTriggerDefinition(
    string TriggerId,
    string TriggerName,
    IReadOnlyList<PluginActionParameterDefinition>? Parameters = null);

public sealed record PluginTriggerEvent(
    string TriggerId,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?> Variables);

/// <summary>
/// 注册并发出插件任务触发事件；注册资源应随插件上下文释放。
/// </summary>
public interface IPluginTaskTriggerApi
{
    IDisposable Register(IReadOnlyList<PluginTriggerDefinition> definitions);

    void Emit(PluginTriggerEvent trigger);
}
