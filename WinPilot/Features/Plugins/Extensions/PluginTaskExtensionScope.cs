using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed class PluginTaskExtensionScope : IPluginTaskTriggerApi, IPluginNotificationVariableApi, IPluginTaskActionApi, IDisposable
{
    private readonly PluginTaskTriggerRegistry triggers;
    private readonly PluginNotificationVariableRegistry variables;
    private readonly PluginTaskActionRegistry actions;
    private readonly string pluginId;
    private readonly string displayName;
    private readonly long generation;
    private readonly object gate = new();
    private readonly List<IDisposable> registrations = [];
    private bool disposed;

    public PluginTaskExtensionScope(
        string pluginId,
        string displayName,
        long generation,
        PluginTaskTriggerRegistry triggers,
        PluginNotificationVariableRegistry variables,
        PluginTaskActionRegistry actions)
    {
        this.pluginId = pluginId;
        this.displayName = displayName;
        this.generation = generation;
        this.triggers = triggers;
        this.variables = variables;
        this.actions = actions;
    }

    public IDisposable Register(IReadOnlyList<PluginTriggerDefinition> definitions) =>
        Add(triggers.Register(pluginId, displayName, generation, definitions));

    public void Emit(PluginTriggerEvent trigger)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
        }
        triggers.Emit(pluginId, generation, trigger, IsCurrent);
    }

    IDisposable IPluginNotificationVariableApi.Register(IReadOnlyList<PluginVariableDefinition> definitions) =>
        Add(variables.Register(pluginId, displayName, generation, definitions, IsCurrent));

    IDisposable IPluginNotificationVariableApi.Register(
        IReadOnlyList<PluginVariableDefinition> definitions,
        PluginNotificationVariableResolver resolver) =>
        Add(variables.Register(pluginId, displayName, generation, definitions, IsCurrent, resolver));

    IDisposable IPluginTaskActionApi.Register(IReadOnlyList<PluginTaskActionDefinition> definitions) =>
        Add(actions.Register(pluginId, displayName, generation, definitions, IsCurrent));

    IDisposable IPluginTaskActionApi.Register(
        IReadOnlyList<PluginTaskActionDefinition> definitions,
        PluginTaskActionHandler handler) =>
        Add(actions.Register(pluginId, displayName, generation, definitions, IsCurrent, handler));

    IDisposable IPluginTaskActionApi.Register(
        IReadOnlyList<PluginTaskActionDefinition> definitions,
        PluginTaskActionHandler handler,
        PluginActionOptionsResolver optionsResolver) =>
        Add(actions.Register(pluginId, displayName, generation, definitions, IsCurrent, handler, optionsResolver));

    private IDisposable Add(IDisposable registration)
    {
        var tracked = new TrackedRegistration(this, registration);
        lock (gate)
        {
            if (!disposed)
            {
                registrations.Add(tracked);
                return tracked;
            }
        }
        tracked.Dispose();
        throw new ObjectDisposedException(nameof(PluginTaskExtensionScope));
    }

    private void Untrack(IDisposable registration)
    {
        lock (gate)
        {
            registrations.Remove(registration);
        }
    }

    private bool IsCurrent()
    {
        lock (gate)
        {
            return !disposed;
        }
    }

    public void Dispose()
    {
        IDisposable[] active;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            active = registrations.ToArray();
            registrations.Clear();
        }
        foreach (var registration in active.Reverse())
        {
            registration.Dispose();
        }
        triggers.Invalidate(pluginId, generation);
        variables.Invalidate(pluginId, generation);
        actions.Invalidate(pluginId, generation);
    }

    private sealed class TrackedRegistration(PluginTaskExtensionScope owner, IDisposable inner) : IDisposable
    {
        private PluginTaskExtensionScope? scope = owner;
        private IDisposable? registration = inner;

        public void Dispose()
        {
            var retired = Interlocked.Exchange(ref registration, null);
            if (retired is null)
            {
                return;
            }
            var owner = Interlocked.Exchange(ref scope, null);
            try
            {
                retired.Dispose();
            }
            finally
            {
                owner?.Untrack(this);
            }
        }
    }
}
