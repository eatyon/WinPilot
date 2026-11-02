using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed class PluginTaskTriggerRegistry(ILogger<PluginTaskTriggerRegistry>? logger)
{
    public PluginTaskTriggerRegistry() : this(null)
    {
    }

    private readonly object gate = new();
    private readonly Dictionary<string, RegisteredPluginTrigger> definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> generationTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> pendingGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GenerationRetirement> retiringGenerations = new(StringComparer.OrdinalIgnoreCase);

    public event Action<PluginTaskTriggerFired>? Triggered;

    /// <summary>
    /// 使旧代次失效并完成其取消交接，再为新运行分配令牌，防止卸载后的触发继续执行。
    /// </summary>
    internal (long Generation, CancellationToken Token) BeginGeneration(string pluginId)
    {
        long generation;
        GenerationRetirement? retirement;
        bool ownsRetirement;
        lock (gate)
        {
            if (retiringGenerations.ContainsKey(pluginId))
            {
                throw new InvalidOperationException("插件正在取消交接，请在交接完成后重试");
            }
            generation = generations.TryGetValue(pluginId, out var current) ? current + 1 : 1;
            generations[pluginId] = generation;
            pendingGenerations[pluginId] = generation;
            (retirement, ownsRetirement) = DetachGeneration(pluginId);
        }
        if (ownsRetirement)
        {
            RetireGeneration(pluginId, retirement!);
        }
        lock (gate)
        {
            if (!pendingGenerations.TryGetValue(pluginId, out var pending)
                || pending != generation
                || generations[pluginId] != generation
                || retiringGenerations.ContainsKey(pluginId))
            {
                return (generation, new CancellationToken(canceled: true));
            }
            var source = new CancellationTokenSource();
            generationTokens[pluginId] = source;
            pendingGenerations.Remove(pluginId);
            return (generation, source.Token);
        }
    }

    internal IDisposable Register(
        string pluginId,
        string displayName,
        long generation,
        IReadOnlyList<PluginTriggerDefinition> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var values = items.ToArray();
        lock (gate)
        {
            if (values.Any(item => string.IsNullOrWhiteSpace(item.TriggerId) || string.IsNullOrWhiteSpace(item.TriggerName)))
            {
                throw new ArgumentException("TriggerId 和 TriggerName 不能为空", nameof(items));
            }
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in values)
            {
                ValidateParameters(item.Parameters ?? []);
                if (!ids.Add(item.TriggerId))
                {
                    throw new InvalidOperationException("TriggerId 重复");
                }
            }
            EnsureCurrent(pluginId, generation);
            if (values.Any(item => definitions.ContainsKey(Key(pluginId, item.TriggerId))))
            {
                throw new InvalidOperationException("TriggerId 重复");
            }
            foreach (var item in values)
            {
                definitions[Key(pluginId, item.TriggerId)] = new(pluginId, displayName, item.TriggerId, item.TriggerName, generation, true, item.Parameters ?? []);
            }
        }
        return new RegistryRegistration(() => Remove(pluginId, generation, values.Select(item => item.TriggerId)));
    }

    internal void Emit(
        string pluginId,
        long generation,
        PluginTriggerEvent trigger,
        Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        Action<PluginTaskTriggerFired>? callback;
        CancellationToken token;
        lock (gate)
        {
            if (!IsCurrent(pluginId, generation)
                || !isCurrent()
                || !definitions.TryGetValue(Key(pluginId, trigger.TriggerId), out var definition)
                || definition.Generation != generation
                || !generationTokens.TryGetValue(pluginId, out var source))
            {
                return;
            }
            token = source.Token;
            callback = Triggered;
        }
        try
        {
            var fired = new PluginTaskTriggerFired(
                pluginId,
                trigger.TriggerId,
                trigger.Timestamp,
                new Dictionary<string, object?>(trigger.Variables, StringComparer.OrdinalIgnoreCase),
                generation,
                token);
            if (!isCurrent() || !IsCurrent(fired))
            {
                return;
            }
            callback?.Invoke(fired);
        }
        catch
        {
        }
    }

    internal bool IsCurrent(PluginTaskTriggerFired trigger)
    {
        lock (gate)
        {
            return !trigger.CancellationToken.IsCancellationRequested
                && IsCurrent(trigger.PluginId, trigger.Generation)
                && definitions.TryGetValue(Key(trigger.PluginId, trigger.TriggerId), out var definition)
                && definition.Generation == trigger.Generation
                && generationTokens.TryGetValue(trigger.PluginId, out var source)
                && source.Token == trigger.CancellationToken;
        }
    }

    public IReadOnlyList<RegisteredPluginTrigger> GetAll()
    {
        lock (gate)
        {
            return definitions.Values.ToArray();
        }
    }

    internal void Invalidate(string pluginId, long generation)
    {
        GenerationRetirement? retirement = null;
        var ownsRetirement = false;
        lock (gate)
        {
            if (generations.TryGetValue(pluginId, out var current) && current == generation)
            {
                pendingGenerations.Remove(pluginId);
                (retirement, ownsRetirement) = DetachGeneration(pluginId);
            }
            foreach (var key in definitions
                .Where(pair => pair.Value.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)
                    && pair.Value.Generation == generation)
                .Select(pair => pair.Key)
                .ToArray())
            {
                definitions.Remove(key);
            }
        }
        if (ownsRetirement)
        {
            RetireGeneration(pluginId, retirement!);
        }
    }

    private (GenerationRetirement? Retirement, bool Owned) DetachGeneration(string pluginId)
    {
        if (!generationTokens.Remove(pluginId, out var source))
        {
            return (retiringGenerations.GetValueOrDefault(pluginId), false);
        }
        var retirement = new GenerationRetirement(source);
        retiringGenerations[pluginId] = retirement;
        return (retirement, true);
    }

    private void RetireGeneration(string pluginId, GenerationRetirement retirement)
    {
        try
        {
            Cancel(retirement.Source, pluginId);
        }
        finally
        {
            lock (gate)
            {
                if (retiringGenerations.TryGetValue(pluginId, out var current) && ReferenceEquals(current, retirement))
                {
                    retiringGenerations.Remove(pluginId);
                }
            }
        }
    }

    private sealed class GenerationRetirement(CancellationTokenSource source)
    {
        internal CancellationTokenSource Source { get; } = source;
    }

    private void Cancel(CancellationTokenSource source, string pluginId)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException exception)
        {
            logger?.LogWarning(exception, "插件触发条件取消回调异常，插件 ID：{PluginId}", pluginId);
        }
        finally
        {
            source.Dispose();
        }
    }

    private void Remove(string pluginId, long generation, IEnumerable<string> ids)
    {
        lock (gate)
        {
            foreach (var id in ids)
            {
                if (definitions.TryGetValue(Key(pluginId, id), out var value) && value.Generation == generation)
                {
                    definitions.Remove(Key(pluginId, id));
                }
            }
        }
    }

    private bool IsCurrent(string pluginId, long generation) =>
        generations.TryGetValue(pluginId, out var current)
        && current == generation
        && generationTokens.ContainsKey(pluginId);

    private void EnsureCurrent(string pluginId, long generation)
    {
        if (!IsCurrent(pluginId, generation))
        {
            throw new InvalidOperationException("插件注册已失效");
        }
    }

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
                    || parameter.Options.Count == 0
                    || parameter.Options.Any(option => string.IsNullOrWhiteSpace(option.Value) || string.IsNullOrWhiteSpace(option.Label))))
            {
                throw new ArgumentException("选择参数必须提供有效选项");
            }
        }
    }

    private static string Key(string pluginId, string id) => pluginId + "\0" + id;
}
