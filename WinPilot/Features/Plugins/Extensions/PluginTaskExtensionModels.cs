using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed record RegisteredPluginTrigger(
    string PluginId,
    string PluginDisplayName,
    string TriggerId,
    string TriggerName,
    long Generation,
    bool Available = true,
    IReadOnlyList<PluginActionParameterDefinition>? Parameters = null);

public sealed record RegisteredPluginVariable(
    string PluginId,
    string PluginDisplayName,
    string Name,
    string DisplayName,
    string Description,
    PluginVariableType Type,
    long Generation,
    bool Available = true);

public sealed record RegisteredPluginAction(
    string PluginId,
    string PluginDisplayName,
    string ActionId,
    string ActionName,
    IReadOnlyList<PluginActionParameterDefinition> Parameters,
    long Generation,
    bool Available = true,
    PluginTaskActionHandler? Handler = null,
    PluginHostActionPlan? HostActionPlan = null,
    PluginActionOptionsResolver? OptionsResolver = null);

public sealed record PluginTaskTriggerFired(
    string PluginId,
    string TriggerId,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?> Variables,
    long Generation,
    CancellationToken CancellationToken = default);
