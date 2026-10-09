using System.Text.RegularExpressions;
using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed record RegisteredPluginTrigger(string PluginId, string PluginDisplayName, string TriggerId, string TriggerName, long Generation, bool Available = true, IReadOnlyList<PluginActionParameterDefinition>? Parameters = null);
public sealed record RegisteredPluginVariable(string PluginId, string PluginDisplayName, string Name, string DisplayName, string Description, PluginVariableType Type, long Generation, bool Available = true);
public sealed record RegisteredPluginAction(string PluginId, string PluginDisplayName, string ActionId, string ActionName, IReadOnlyList<PluginActionParameterDefinition> Parameters, long Generation, bool Available = true, PluginTaskActionHandler? Handler = null, PluginHostActionPlan? HostActionPlan = null, PluginActionOptionsResolver? OptionsResolver = null);
public sealed record PluginTaskTriggerFired(string PluginId, string TriggerId, DateTimeOffset Timestamp, IReadOnlyDictionary<string, object?> Variables, long Generation, CancellationToken CancellationToken = default);

public sealed class PluginTaskExtensionRegistry(PluginTaskTriggerRegistry triggers, PluginNotificationVariableRegistry variables, PluginTaskActionRegistry actions)
{
    public event Action<PluginTaskTriggerFired>? Triggered { add => triggers.Triggered += value; remove => triggers.Triggered -= value; }
    public PluginTaskExtensionScope CreateScope(string pluginId, string displayName) { var generation = triggers.BeginGeneration(pluginId); return new(pluginId, displayName, generation.Generation, triggers, variables, actions); }
    public IReadOnlyList<RegisteredPluginTrigger> GetTriggers() => triggers.GetAll();
    public IReadOnlyList<RegisteredPluginVariable> GetVariables() => variables.GetAll();
    internal Task<IReadOnlyDictionary<string, object?>> ResolveVariablesAsync(IReadOnlyList<string> names, PluginTaskRunContext context, CancellationToken cancellationToken) => variables.ResolveAsync(names, context, cancellationToken);
    public IReadOnlyList<RegisteredPluginAction> GetActions() => actions.GetAll();
    public Task<IReadOnlyList<PluginActionOption>> GetActionOptionsAsync(string pluginId, string actionId, string parameterKey, CancellationToken cancellationToken) => actions.GetOptionsAsync(pluginId, actionId, parameterKey, cancellationToken);
    internal PluginTaskActionRegistry Actions => actions;
}

public sealed class PluginTaskTriggerRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, RegisteredPluginTrigger> definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> generationTokens = new(StringComparer.OrdinalIgnoreCase);
    public event Action<PluginTaskTriggerFired>? Triggered;

    internal (long Generation, CancellationToken Token) BeginGeneration(string pluginId) { lock (gate) { if (generationTokens.TryGetValue(pluginId, out var previous)) { try { previous.Cancel(); } finally { previous.Dispose(); } } var generation = generations.TryGetValue(pluginId, out var current) ? current + 1 : 1; generations[pluginId] = generation; var source = new CancellationTokenSource(); generationTokens[pluginId] = source; return (generation, source.Token); } }
    internal IDisposable Register(string pluginId, string displayName, long generation, IReadOnlyList<PluginTriggerDefinition> items)
    {
        ArgumentNullException.ThrowIfNull(items); var values = items.ToArray(); if (values.Any(item => string.IsNullOrWhiteSpace(item.TriggerId) || string.IsNullOrWhiteSpace(item.TriggerName))) throw new ArgumentException("TriggerId 和 TriggerName 不能为空", nameof(items));
        lock (gate) { EnsureCurrent(pluginId, generation); if (values.Any(item => definitions.ContainsKey(Key(pluginId, item.TriggerId)))) throw new InvalidOperationException("TriggerId 重复"); foreach (var item in values) { ValidateParameters(item.Parameters ?? []); definitions[Key(pluginId, item.TriggerId)] = new(pluginId, displayName, item.TriggerId, item.TriggerName, generation, true, item.Parameters ?? []); } }
        return new RegistryRegistration(() => Remove(pluginId, generation, values.Select(item => item.TriggerId)));
    }
    internal void Emit(string pluginId, long generation, PluginTriggerEvent trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger); Action<PluginTaskTriggerFired>? callback; lock (gate) { if (!IsCurrent(pluginId, generation) || !definitions.TryGetValue(Key(pluginId, trigger.TriggerId), out var definition) || definition.Generation != generation) return; callback = Triggered; }
        try { CancellationToken token; lock (gate) token = generationTokens.TryGetValue(pluginId, out var source) ? source.Token : default; callback?.Invoke(new(pluginId, trigger.TriggerId, trigger.Timestamp, new Dictionary<string, object?>(trigger.Variables, StringComparer.OrdinalIgnoreCase), generation, token)); } catch { }
    }
    public IReadOnlyList<RegisteredPluginTrigger> GetAll() { lock (gate) return definitions.Values.ToArray(); }
    internal void Invalidate(string pluginId, long generation) { lock (gate) { if (generations.TryGetValue(pluginId, out var current) && current == generation && generationTokens.TryGetValue(pluginId, out var source)) { source.Cancel(); source.Dispose(); generationTokens.Remove(pluginId); } foreach (var key in definitions.Where(pair => pair.Value.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase) && pair.Value.Generation == generation).Select(pair => pair.Key).ToArray()) definitions.Remove(key); } }
    private void Remove(string pluginId, long generation, IEnumerable<string> ids) { lock (gate) foreach (var id in ids) if (definitions.TryGetValue(Key(pluginId, id), out var value) && value.Generation == generation) definitions.Remove(Key(pluginId, id)); }
    private bool IsCurrent(string pluginId, long generation) => generations.TryGetValue(pluginId, out var current) && current == generation;
    private void EnsureCurrent(string pluginId, long generation) { if (!IsCurrent(pluginId, generation)) throw new InvalidOperationException("插件注册已失效"); }
    private static void ValidateParameters(IReadOnlyList<PluginActionParameterDefinition> parameters) { var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var parameter in parameters) { if (string.IsNullOrWhiteSpace(parameter.Key) || !keys.Add(parameter.Key)) throw new ArgumentException("参数名称为空或重复"); if (parameter.Type is PluginActionParameterType.Select or PluginActionParameterType.MultiCheckbox && (parameter.Options is null || parameter.Options.Count == 0 || parameter.Options.Any(option => string.IsNullOrWhiteSpace(option.Value) || string.IsNullOrWhiteSpace(option.Label)))) throw new ArgumentException("选择参数必须提供有效选项"); } }
    private static string Key(string pluginId, string id) => pluginId + "\0" + id;
}

public sealed class PluginNotificationVariableRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, RegisteredPluginVariable> definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolverRegistration> resolvers = new(StringComparer.OrdinalIgnoreCase);

    internal IDisposable Register(string pluginId, string displayName, long generation,
        IReadOnlyList<PluginVariableDefinition> items, Func<bool> isCurrent,
        PluginNotificationVariableResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var values = items.ToArray();
        if (values.Any(item => !Regex.IsMatch(item.Name ?? string.Empty, "^[\\$&][a-z][a-z0-9-]*(\\.[a-z][a-z0-9_-]*)+$")
            || string.IsNullOrWhiteSpace(item.DisplayName)))
            throw new ArgumentException("变量名称或显示名称无效", nameof(items));
        if (values.Any(item => item.Type == PluginVariableType.Image ? !item.Name.StartsWith('&') : !item.Name.StartsWith('$')))
            throw new ArgumentException("变量前缀与变量类型不匹配", nameof(items));
        lock (gate)
        {
            if (!isCurrent()) throw new InvalidOperationException("插件注册已失效");
            if (values.Any(item => definitions.ContainsKey(item.Name))) throw new InvalidOperationException("变量名称重复");
            foreach (var item in values)
            {
                definitions[item.Name] = new(pluginId, displayName, item.Name, item.DisplayName, item.Description, item.Type, generation);
                if (resolver is not null) resolvers[item.Name] = new(pluginId, generation, resolver);
            }
        }
        return new RegistryRegistration(() => Remove(pluginId, generation, values));
    }

    internal async Task<IReadOnlyDictionary<string, object?>> ResolveAsync(IReadOnlyList<string> names,
        PluginTaskRunContext context, CancellationToken cancellationToken)
    {
        ResolverRequest[] requests;
        lock (gate)
        {
            requests = names.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => resolvers.ContainsKey(name))
                .GroupBy(name => resolvers[name])
                .Select(group => new ResolverRequest(group.Key, group.ToArray()))
                .ToArray();
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await request.Registration.Resolver(request.Names, context, cancellationToken);
            foreach (var name in request.Names)
                if (values.TryGetValue(name, out var value))
                {
                    RegisteredPluginVariable? definition;
                    lock (gate)
                        definitions.TryGetValue(name, out definition);
                    if (definition is null || definition.Generation != request.Registration.Generation
                        || !definition.PluginId.Equals(request.Registration.PluginId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (definition.Type == PluginVariableType.Image && value is not null and not PluginImageContent)
                        throw new InvalidOperationException($"图片变量 {name} 返回了无效内容");
                    if (definition.Type == PluginVariableType.Text && value is PluginImageContent)
                        throw new InvalidOperationException($"文字变量 {name} 不能返回图片内容");
                    result[name] = value;
                }
        }
        return result;
    }

    public IReadOnlyList<RegisteredPluginVariable> GetAll() { lock (gate) return definitions.Values.ToArray(); }

    internal void Invalidate(string pluginId, long generation)
    {
        lock (gate)
            foreach (var key in definitions.Where(pair => pair.Value.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)
                && pair.Value.Generation == generation).Select(pair => pair.Key).ToArray())
            {
                definitions.Remove(key);
                resolvers.Remove(key);
            }
    }

    private void Remove(string pluginId, long generation, IReadOnlyList<PluginVariableDefinition> values)
    {
        lock (gate)
            foreach (var item in values)
                if (definitions.TryGetValue(item.Name, out var value) && value.Generation == generation
                    && value.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
                {
                    definitions.Remove(item.Name);
                    resolvers.Remove(item.Name);
                }
    }

    private sealed record ResolverRegistration(string PluginId, long Generation, PluginNotificationVariableResolver Resolver);
    private sealed record ResolverRequest(ResolverRegistration Registration, string[] Names);
}

public sealed class PluginTaskActionRegistry
{
    private readonly object gate = new(); private readonly Dictionary<string, RegisteredPluginAction> definitions = new(StringComparer.OrdinalIgnoreCase);
    internal IDisposable Register(string pluginId, string displayName, long generation, IReadOnlyList<PluginTaskActionDefinition> items, Func<bool> isCurrent, PluginTaskActionHandler? handler = null, PluginActionOptionsResolver? optionsResolver = null)
    {
        ArgumentNullException.ThrowIfNull(items); var values = items.ToArray(); if (values.Any(item => string.IsNullOrWhiteSpace(item.ActionId) || string.IsNullOrWhiteSpace(item.ActionName))) throw new ArgumentException("ActionId 和 ActionName 不能为空", nameof(items));
        lock (gate)
        {
            if (!isCurrent()) throw new InvalidOperationException("插件注册已失效");
            if (values.Any(item => definitions.ContainsKey(Key(pluginId, item.ActionId)))) throw new InvalidOperationException("ActionId 重复");
            foreach (var item in values)
            {
                ValidateParameters(item.Parameters ?? []);
                ValidatePlan(item.HostActionPlan);
                PluginActionOptionsResolver? currentResolver = optionsResolver is null ? null : async (actionId, parameterKey, cancellationToken) =>
                {
                    if (!isCurrent()) throw new InvalidOperationException("插件注册已失效");
                    var result = await optionsResolver(actionId, parameterKey, cancellationToken);
                    if (!isCurrent()) throw new InvalidOperationException("插件注册已失效");
                    return result;
                };
                definitions[Key(pluginId, item.ActionId)] = new(pluginId, displayName, item.ActionId, item.ActionName, item.Parameters ?? [], generation, true, handler, item.HostActionPlan, currentResolver);
            }
        }
        return new RegistryRegistration(() => { lock (gate) foreach (var item in values) if (definitions.TryGetValue(Key(pluginId, item.ActionId), out var value) && value.Generation == generation) definitions.Remove(Key(pluginId, item.ActionId)); });
    }
    public IReadOnlyList<RegisteredPluginAction> GetAll() { lock (gate) return definitions.Values.ToArray(); }
    internal bool TryGet(string pluginId, string actionId, out RegisteredPluginAction action) { lock (gate) return definitions.TryGetValue(Key(pluginId, actionId), out action!); }
    internal async Task<IReadOnlyList<PluginActionOption>> GetOptionsAsync(string pluginId, string actionId, string parameterKey, CancellationToken cancellationToken)
    {
        if (!TryGet(pluginId, actionId, out var action)) throw new FileNotFoundException("扩展动作不可用");
        var parameter = action.Parameters.FirstOrDefault(item => item.Key.Equals(parameterKey, StringComparison.Ordinal));
        if (parameter is null || parameter.Type is not (PluginActionParameterType.Select or PluginActionParameterType.MultiCheckbox))
            throw new InvalidOperationException("动作选择参数不存在");
        var options = action.OptionsResolver is null ? parameter.Options ?? [] : await action.OptionsResolver(actionId, parameterKey, cancellationToken);
        if (options is null || options.Any(option => option is null || string.IsNullOrWhiteSpace(option.Value) || string.IsNullOrWhiteSpace(option.Label)))
            throw new InvalidOperationException("动作选择参数包含无效选项");
        return options;
    }
    internal void Invalidate(string pluginId, long generation) { lock (gate) foreach (var key in definitions.Where(pair => pair.Value.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase) && pair.Value.Generation == generation).Select(pair => pair.Key).ToArray()) definitions.Remove(key); }
    private static string Key(string pluginId, string id) => pluginId + "\0" + id;
    private static void ValidateParameters(IReadOnlyList<PluginActionParameterDefinition> parameters) { var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var parameter in parameters) { if (string.IsNullOrWhiteSpace(parameter.Key) || !keys.Add(parameter.Key)) throw new ArgumentException("参数名称为空或重复"); if (parameter.Type is PluginActionParameterType.Select or PluginActionParameterType.MultiCheckbox && (parameter.Options is null || parameter.Options.Any(option => string.IsNullOrWhiteSpace(option.Value) || string.IsNullOrWhiteSpace(option.Label)))) throw new ArgumentException("选择参数必须提供有效选项"); } }
    internal static void ValidatePlan(PluginHostActionPlan? plan) { if (plan is null) return; if (plan.Steps is null || plan.Steps.Count == 0) throw new ArgumentException("Host Action Plan 不能为空"); foreach (var step in plan.Steps) { if (!Enum.IsDefined(step.Type) || step.Type == PluginHostActionType.StartProgram && !step.Parameters.ContainsKey("programPath") || step.Parameters.Keys.Any(key => key.StartsWith("extension", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Host Action Plan 包含不支持的步骤"); } }
}

public sealed class PluginTaskExtensionScope : IPluginTaskTriggerApi, IPluginNotificationVariableApi, IPluginTaskActionApi, IDisposable
{
    private readonly PluginTaskTriggerRegistry triggers; private readonly PluginNotificationVariableRegistry variables; private readonly PluginTaskActionRegistry actions; private readonly string pluginId; private readonly string displayName; private readonly long generation; private readonly object gate = new(); private readonly List<IDisposable> registrations = []; private bool disposed;
    public PluginTaskExtensionScope(string pluginId, string displayName, long generation, PluginTaskTriggerRegistry triggers, PluginNotificationVariableRegistry variables, PluginTaskActionRegistry actions) { this.pluginId = pluginId; this.displayName = displayName; this.generation = generation; this.triggers = triggers; this.variables = variables; this.actions = actions; }
    public IDisposable Register(IReadOnlyList<PluginTriggerDefinition> definitions) => Add(triggers.Register(pluginId, displayName, generation, definitions));
    public void Emit(PluginTriggerEvent trigger) { lock (gate) if (!disposed) triggers.Emit(pluginId, generation, trigger); }
    IDisposable IPluginNotificationVariableApi.Register(IReadOnlyList<PluginVariableDefinition> definitions) => Add(variables.Register(pluginId, displayName, generation, definitions, IsCurrent));
    IDisposable IPluginNotificationVariableApi.Register(IReadOnlyList<PluginVariableDefinition> definitions, PluginNotificationVariableResolver resolver) => Add(variables.Register(pluginId, displayName, generation, definitions, IsCurrent, resolver));
    IDisposable IPluginTaskActionApi.Register(IReadOnlyList<PluginTaskActionDefinition> definitions) => Add(actions.Register(pluginId, displayName, generation, definitions, IsCurrent));
    IDisposable IPluginTaskActionApi.Register(IReadOnlyList<PluginTaskActionDefinition> definitions, PluginTaskActionHandler handler) => Add(actions.Register(pluginId, displayName, generation, definitions, IsCurrent, handler));
    IDisposable IPluginTaskActionApi.Register(IReadOnlyList<PluginTaskActionDefinition> definitions, PluginTaskActionHandler handler, PluginActionOptionsResolver optionsResolver) => Add(actions.Register(pluginId, displayName, generation, definitions, IsCurrent, handler, optionsResolver));
    private IDisposable Add(IDisposable registration) { lock (gate) { if (disposed) { registration.Dispose(); throw new ObjectDisposedException(nameof(PluginTaskExtensionScope)); } registrations.Add(registration); } return registration; }
    private bool IsCurrent() { lock (gate) return !disposed; }
    public void Dispose() { IDisposable[] active; lock (gate) { if (disposed) return; disposed = true; active = registrations.ToArray(); registrations.Clear(); } foreach (var registration in active.Reverse()) registration.Dispose(); triggers.Invalidate(pluginId, generation); variables.Invalidate(pluginId, generation); actions.Invalidate(pluginId, generation); }
}

internal sealed class RegistryRegistration(Action dispose) : IDisposable
{
    private Action? action = dispose;
    public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
}
