using System.Text.RegularExpressions;
using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed class PluginNotificationVariableRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, RegisteredPluginVariable> definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolverRegistration> resolvers = new(StringComparer.OrdinalIgnoreCase);

    internal IDisposable Register(
        string pluginId,
        string displayName,
        long generation,
        IReadOnlyList<PluginVariableDefinition> items,
        Func<bool> isCurrent,
        PluginNotificationVariableResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var values = items.ToArray();
        if (values.Any(item =>
            !Regex.IsMatch(item.Name ?? string.Empty, "^[\\$&][a-z][a-z0-9-]*(\\.[a-z][a-z0-9_-]*)+$")
            || string.IsNullOrWhiteSpace(item.DisplayName)))
        {
            throw new ArgumentException("变量名称或显示名称无效", nameof(items));
        }
        if (values.Any(item => item.Type == PluginVariableType.Image ? !item.Name.StartsWith('&') : !item.Name.StartsWith('$')))
        {
            throw new ArgumentException("变量前缀与变量类型不匹配", nameof(items));
        }
        lock (gate)
        {
            if (!isCurrent())
            {
                throw new InvalidOperationException("插件注册已失效");
            }
            if (values.Any(item => definitions.ContainsKey(item.Name)))
            {
                throw new InvalidOperationException("变量名称重复");
            }
            foreach (var item in values)
            {
                definitions[item.Name] = new(pluginId, displayName, item.Name, item.DisplayName, item.Description, item.Type, generation);
                if (resolver is not null)
                {
                    resolvers[item.Name] = new(pluginId, generation, resolver);
                }
            }
        }
        return new RegistryRegistration(() => Remove(pluginId, generation, values));
    }

    internal async Task<IReadOnlyDictionary<string, object?>> ResolveAsync(
        IReadOnlyList<string> names,
        PluginTaskRunContext context,
        CancellationToken cancellationToken)
    {
        ResolverRequest[] requests;
        lock (gate)
        {
            requests = names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => resolvers.ContainsKey(name))
                .GroupBy(name => resolvers[name])
                .Select(group =>
                    new ResolverRequest(
                        group.Key,
                        group.ToArray(),
                        group.ToDictionary(name => name, name => resolvers[name], StringComparer.OrdinalIgnoreCase)))
                .ToArray();
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await request.Registration.Resolver(request.Names, context, cancellationToken);
            foreach (var name in request.Names)
            {
                if (values.TryGetValue(name, out var value))
                {
                    lock (gate)
                    {
                        // 等待插件解析期间可能发生重启，旧注册返回的值不能应用到新代次。
                        if (!resolvers.TryGetValue(name, out var current)
                            || !ReferenceEquals(current, request.Registrations[name])
                            || !definitions.TryGetValue(name, out var definition)
                            || definition.Generation != request.Registration.Generation
                            || !definition.PluginId.Equals(request.Registration.PluginId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (definition.Type == PluginVariableType.Image && value is not null and not PluginImageContent)
                        {
                            throw new InvalidOperationException($"图片变量 {name} 返回了无效内容");
                        }
                        if (definition.Type == PluginVariableType.Text && value is PluginImageContent)
                        {
                            throw new InvalidOperationException($"文字变量 {name} 不能返回图片内容");
                        }
                        result[name] = value;
                    }
                }
            }
        }
        return result;
    }

    public IReadOnlyList<RegisteredPluginVariable> GetAll()
    {
        lock (gate)
        {
            return definitions.Values.ToArray();
        }
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
                resolvers.Remove(key);
            }
        }
    }

    private void Remove(string pluginId, long generation, IReadOnlyList<PluginVariableDefinition> values)
    {
        lock (gate)
        {
            foreach (var item in values)
            {
                if (definitions.TryGetValue(item.Name, out var value)
                    && value.Generation == generation
                    && value.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
                {
                    definitions.Remove(item.Name);
                    resolvers.Remove(item.Name);
                }
            }
        }
    }

    private sealed record ResolverRegistration(
        string PluginId,
        long Generation,
        PluginNotificationVariableResolver Resolver);

    private sealed record ResolverRequest(
        ResolverRegistration Registration,
        string[] Names,
        IReadOnlyDictionary<string, ResolverRegistration> Registrations);
}
