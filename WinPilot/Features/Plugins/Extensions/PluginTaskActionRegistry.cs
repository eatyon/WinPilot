using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed class PluginTaskActionRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, RegisteredPluginAction> definitions = new(StringComparer.OrdinalIgnoreCase);

    internal IDisposable Register(
        string pluginId,
        string displayName,
        long generation,
        IReadOnlyList<PluginTaskActionDefinition> items,
        Func<bool> isCurrent,
        PluginTaskActionHandler? handler = null,
        PluginActionOptionsResolver? optionsResolver = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var values = items.ToArray();
        lock (gate)
        {
            if (values.Any(item => string.IsNullOrWhiteSpace(item.ActionId) || string.IsNullOrWhiteSpace(item.ActionName)))
            {
                throw new ArgumentException("ActionId 和 ActionName 不能为空", nameof(items));
            }
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in values)
            {
                ValidateParameters(item.Parameters ?? []);
                ValidatePlan(item.HostActionPlan);
                if (!ids.Add(item.ActionId))
                {
                    throw new InvalidOperationException("ActionId 重复");
                }
            }
            if (!isCurrent())
            {
                throw new InvalidOperationException("插件注册已失效");
            }
            if (values.Any(item => definitions.ContainsKey(Key(pluginId, item.ActionId))))
            {
                throw new InvalidOperationException("ActionId 重复");
            }
            foreach (var item in values)
            {
                PluginActionOptionsResolver? currentResolver = optionsResolver is null
                    ? null
                    : async (actionId, parameterKey, cancellationToken) =>
                {
                    if (!isCurrent())
                    {
                        throw new InvalidOperationException("插件注册已失效");
                    }
                    var result = await optionsResolver(actionId, parameterKey, cancellationToken);
                    if (!isCurrent())
                    {
                        throw new InvalidOperationException("插件注册已失效");
                    }
                    return result;
                };
                definitions[Key(pluginId, item.ActionId)] = new(
                    pluginId,
                    displayName,
                    item.ActionId,
                    item.ActionName,
                    item.Parameters ?? [],
                    generation,
                    true,
                    handler,
                    item.HostActionPlan,
                    currentResolver);
            }
        }
        return new RegistryRegistration(() =>
        {
            lock (gate)
            {
                foreach (var item in values)
                {
                    if (definitions.TryGetValue(Key(pluginId, item.ActionId), out var value)
                        && value.Generation == generation)
                    {
                        definitions.Remove(Key(pluginId, item.ActionId));
                    }
                }
            }
        });
    }

    public IReadOnlyList<RegisteredPluginAction> GetAll()
    {
        lock (gate)
        {
            return definitions.Values.ToArray();
        }
    }

    internal bool TryGet(string pluginId, string actionId, out RegisteredPluginAction action)
    {
        lock (gate)
        {
            return definitions.TryGetValue(Key(pluginId, actionId), out action!);
        }
    }

    internal async Task<IReadOnlyList<PluginActionOption>> GetOptionsAsync(
        string pluginId,
        string actionId,
        string parameterKey,
        CancellationToken cancellationToken)
    {
        if (!TryGet(pluginId, actionId, out var action))
        {
            throw new FileNotFoundException("扩展动作不可用");
        }
        var parameter = action.Parameters.FirstOrDefault(item => item.Key.Equals(parameterKey, StringComparison.Ordinal));
        if (parameter is null
            || parameter.Type is not (PluginActionParameterType.Select or PluginActionParameterType.MultiCheckbox))
        {
            throw new InvalidOperationException("动作选择参数不存在");
        }
        var options = action.OptionsResolver is null
            ? parameter.Options ?? []
            : await action.OptionsResolver(actionId, parameterKey, cancellationToken);
        if (options is null
            || options.Any(option => option is null || string.IsNullOrWhiteSpace(option.Value) || string.IsNullOrWhiteSpace(option.Label)))
        {
            throw new InvalidOperationException("动作选择参数包含无效选项");
        }
        return options;
    }

    internal void Invalidate(string pluginId, long generation)
    {
        lock (gate)
        {
            foreach (var key in definitions
                .Where(pair => pair.Value.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)
                    && pair.Value.Generation == generation)
                .Select(pair => pair.Key)
                .ToArray())
            {
                definitions.Remove(key);
            }
        }
    }

    private static string Key(string pluginId, string id) => pluginId + "\0" + id;

    private static void ValidateParameters(IReadOnlyList<PluginActionParameterDefinition> parameters)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Key) || !keys.Add(parameter.Key))
            {
                throw new ArgumentException("参数名称为空或重复");
            }
            if (parameter.Type is PluginActionParameterType.Select or PluginActionParameterType.MultiCheckbox
                && (parameter.Options is null
                    || parameter.Options.Any(option => string.IsNullOrWhiteSpace(option.Value) || string.IsNullOrWhiteSpace(option.Label))))
            {
                throw new ArgumentException("选择参数必须提供有效选项");
            }
        }
    }

    internal static void ValidatePlan(PluginHostActionPlan? plan)
    {
        if (plan is null)
        {
            return;
        }
        if (plan.Steps is null || plan.Steps.Count == 0)
        {
            throw new ArgumentException("Host Action Plan 不能为空");
        }
        foreach (var step in plan.Steps)
        {
            if (!Enum.IsDefined(step.Type)
                || step.Type == PluginHostActionType.StartProgram
                    && !step.Parameters.ContainsKey("programPath")
                || step.Parameters.Keys.Any(key => key.StartsWith("extension", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Host Action Plan 包含不支持的步骤");
            }
        }
    }
}
