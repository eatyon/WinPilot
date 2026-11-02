using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

/// <summary>
/// 汇总插件触发器、通知变量和动作注册，为每次插件运行创建独立代次的注册作用域。
/// </summary>
public sealed class PluginTaskExtensionRegistry(
    PluginTaskTriggerRegistry triggers,
    PluginNotificationVariableRegistry variables,
    PluginTaskActionRegistry actions)
{
    public event Action<PluginTaskTriggerFired>? Triggered { add => triggers.Triggered += value; remove => triggers.Triggered -= value; }

    public PluginTaskExtensionScope CreateScope(string pluginId, string displayName)
    {
        var generation = triggers.BeginGeneration(pluginId);
        return new(pluginId, displayName, generation.Generation, triggers, variables, actions);
    }

    public IReadOnlyList<RegisteredPluginTrigger> GetTriggers() => triggers.GetAll();

    internal bool IsCurrent(PluginTaskTriggerFired trigger) => triggers.IsCurrent(trigger);

    public IReadOnlyList<RegisteredPluginVariable> GetVariables() => variables.GetAll();

    internal Task<IReadOnlyDictionary<string, object?>> ResolveVariablesAsync(
        IReadOnlyList<string> names,
        PluginTaskRunContext context,
        CancellationToken cancellationToken) =>
        variables.ResolveAsync(names, context, cancellationToken);

    public IReadOnlyList<RegisteredPluginAction> GetActions() => actions.GetAll();

    public Task<IReadOnlyList<PluginActionOption>> GetActionOptionsAsync(
        string pluginId,
        string actionId,
        string parameterKey,
        CancellationToken cancellationToken) =>
        actions.GetOptionsAsync(pluginId, actionId, parameterKey, cancellationToken);

    internal PluginTaskActionRegistry Actions => actions;
}
