using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.PluginContract;
using WinPilot.Features.Notifications;

namespace WinPilot.Features.Plugins;

public sealed record RegisteredPluginCommand(string PluginId, string PluginDisplayName, PluginCommandDefinition Definition);

public sealed class PluginCommandRegistry(Func<string, bool> isReserved, ILogger<PluginCommandRegistry>? logger = null)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> wildcardCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Scope> scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> executions = [];
    private long registrationOrder;
    private readonly ILogger logger = logger ?? NullLogger<PluginCommandRegistry>.Instance;

    public Scope CreateScope(string pluginId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Scope? previous;
        Scope scope;
        lock (gate)
        {
            scopes.TryGetValue(pluginId, out previous);
            scope = new(this, pluginId, displayName);
            scopes[pluginId] = scope;
        }
        previous?.Dispose();
        return scope;
    }

    public IReadOnlyList<RegisteredPluginCommand> GetAll()
    {
        lock (gate) return commands.Values.Concat(wildcardCommands.Values)
            .Where(IsCurrent)
            .OrderBy(entry => entry.Command.PluginDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.RegistrationOrder)
            .Select(entry => entry.Command).ToArray();
    }

    internal string? GetCommandName(string text)
    {
        text = text.Trim();
        lock (gate) return Find(text)?.Command.Definition.Name
            ?? (text.Length > 0 && !isReserved(text) && wildcardCommands.Values.Any(IsCurrent) ? "*" : null);
    }

    internal async Task<bool> ExecuteAsync(string text, Func<CancellationToken, IPluginCommandReply> createReply, CancellationToken token,
        IEnumerable<string>? diagnosticSecrets = null)
    {
        (Entry Entry, CancellationTokenSource Cancellation)? registered = null;
        (Entry Entry, CancellationTokenSource Cancellation)[] handlers = [];
        text = text.Trim();
        lock (gate)
        {
            var entry = Find(text);
            if (entry is not null)
                registered = (entry, CancellationTokenSource.CreateLinkedTokenSource(token, entry.Cancellation.Token));
            else if (text.Length > 0 && !isReserved(text))
                handlers = wildcardCommands.Values.Where(IsCurrent)
                    .Select(item => (item, CancellationTokenSource.CreateLinkedTokenSource(token, item.Cancellation.Token))).ToArray();
        }
        if (registered is not { } matched)
        {
            await Task.WhenAll(handlers.Select(handler => ExecuteWildcardAsync(handler.Entry, handler.Cancellation, text, createReply, token, diagnosticSecrets)));
            return handlers.Length > 0;
        }
        var cancellation = matched.Cancellation;
        IPluginCommandReply reply;
        try { cancellation.Token.ThrowIfCancellationRequested(); reply = createReply(cancellation.Token); }
        catch { cancellation.Dispose(); throw; }
        var closed = 0;
        void CloseReply()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try { (reply as IDisposable)?.Dispose(); }
            catch (Exception exception)
            {
                logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "插件「{PluginName}」命令回复清理失败", matched.Entry.Command.PluginDisplayName);
                throw;
            }
        }
        await TrackExecution(() => InvokeHandlerAsync(matched.Entry, cancellation, text, reply, CloseReply, false, diagnosticSecrets));
        return true;
    }

    private async Task ExecuteWildcardAsync(Entry entry, CancellationTokenSource cancellation, string text,
        Func<CancellationToken, IPluginCommandReply> createReply, CancellationToken callerToken, IEnumerable<string>? diagnosticSecrets)
    {
        cancellation.CancelAfter(TimeSpan.FromSeconds(60));
        var executionToken = cancellation.Token;
        IPluginCommandReply? reply = null;
        Task? actual = null;
        var closed = 0;
        void CloseReply()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try { (reply as IDisposable)?.Dispose(); }
            catch (Exception exception)
            {
                logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "插件「{PluginName}」通配命令回复清理失败", entry.Command.PluginDisplayName);
            }
        }
        try
        {
            executionToken.ThrowIfCancellationRequested();
            reply = createReply(executionToken);
            actual = TrackExecution(() => InvokeHandlerAsync(entry, cancellation, text, reply, CloseReply, true, diagnosticSecrets));
            await actual.WaitAsync(executionToken);
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            if (!callerToken.IsCancellationRequested && !entry.Cancellation.IsCancellationRequested)
                logger.LogWarning("插件「{PluginName}」通配命令处理或发送回复超时", entry.Command.PluginDisplayName);
        }
        catch (Exception exception)
        {
            logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "插件「{PluginName}」通配命令处理失败", entry.Command.PluginDisplayName);
        }
        finally
        {
            CloseReply();
            if (actual is null) cancellation.Dispose();
        }
    }

    private Task TrackExecution(Func<Task> invoke)
    {
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = started.Task.Unwrap();
        lock (gate) executions.Add(execution);
        _ = execution.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (gate) executions.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { started.TrySetResult(invoke()); }
        catch (Exception exception) { started.TrySetException(exception); }
        return execution;
    }

    private async Task InvokeHandlerAsync(Entry entry, CancellationTokenSource cancellation, string text, IPluginCommandReply reply,
        Action closeReply, bool wildcard, IEnumerable<string>? diagnosticSecrets)
    {
        var token = cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var context = new PluginCommandContext(entry.Command.Definition.Name,
                wildcard ? text : text[entry.Command.Definition.Name.Length..].Trim(), reply);
            if (wildcard) await Task.Run(() => entry.Handler(context, token), token);
            else await entry.Handler(context, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (wildcard)
                logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "插件「{PluginName}」通配命令处理失败", entry.Command.PluginDisplayName);
            else
            {
                logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "插件「{PluginName}」命令「{Name}」处理失败",
                    entry.Command.PluginDisplayName, entry.Command.Definition.Name);
                throw;
            }
        }
        finally { try { closeReply(); } finally { cancellation.Dispose(); } }
    }

    private bool IsCurrent(Entry entry) => scopes.TryGetValue(entry.Scope.PluginId, out var scope) && ReferenceEquals(scope, entry.Scope);

    private Entry? Find(string text) => commands.Values
        .Where(IsCurrent)
        .Where(entry => Matches(entry.Command.Definition, text))
        .MaxBy(entry => entry.Command.Definition.Name.Length);

    private static bool Matches(PluginCommandDefinition definition, string text) =>
        text.Equals(definition.Name, StringComparison.OrdinalIgnoreCase) ||
        definition.Parameters is not null && text.StartsWith(definition.Name, StringComparison.OrdinalIgnoreCase) &&
        text.Length > definition.Name.Length &&
        (definition.AllowAttachedArguments || char.IsWhiteSpace(text[definition.Name.Length]));

    private IDisposable Register(Scope scope, PluginCommandDefinition definition, PluginCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        var wildcard = definition.Name == "*";
        if (wildcard)
        {
            if (definition.Parameters is not null || definition.AllowAttachedArguments)
                throw new ArgumentException("通配命令不能声明参数", nameof(definition));
            definition = definition with { Description = "处理未匹配的命令" };
        }
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Any(char.IsWhiteSpace) || definition.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(definition.Description) || definition.Parameters is not null && string.IsNullOrWhiteSpace(definition.Parameters) ||
            definition.AllowAttachedArguments && definition.Parameters is null)
            throw new ArgumentException("命令名称、简介或参数声明无效", nameof(definition));
        lock (gate)
        {
            if (!scopes.TryGetValue(scope.PluginId, out var current) || !ReferenceEquals(current, scope))
                throw new InvalidOperationException("插件命令注册已失效");
            var registrations = wildcard ? wildcardCommands : commands;
            var key = wildcard ? scope.PluginId : definition.Name;
            var reserved = !wildcard && isReserved(definition.Name);
            if (reserved || registrations.TryGetValue(key, out _))
            {
                var owner = reserved ? "WinPilot" : $"{registrations[key].Command.PluginDisplayName} ({registrations[key].Command.PluginId})";
                logger.LogWarning("插件 {PluginDisplayName} ({PluginId}) 命令「{Name}」已跳过：名称已被 {Owner} 占用",
                    scope.DisplayName, scope.PluginId, definition.Name, owner);
                return new Registration(static () => { });
            }
            var entry = new Entry(scope, new(scope.PluginId, scope.DisplayName, definition), handler, ++registrationOrder);
            registrations.Add(key, entry);
            return new Registration(() => Remove(entry));
        }
    }

    private void Remove(Entry entry)
    {
        lock (gate)
        {
            var registrations = entry.Command.Definition.Name == "*" ? wildcardCommands : commands;
            var key = entry.Command.Definition.Name == "*" ? entry.Scope.PluginId : entry.Command.Definition.Name;
            if (!registrations.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
            registrations.Remove(key);
        }
        Cancel(entry);
    }

    private void Remove(Scope scope)
    {
        Entry[] removed;
        lock (gate)
        {
            if (scopes.TryGetValue(scope.PluginId, out var current) && ReferenceEquals(current, scope)) scopes.Remove(scope.PluginId);
            removed = commands.Values.Concat(wildcardCommands.Values).Where(entry => ReferenceEquals(entry.Scope, scope)).ToArray();
            foreach (var entry in removed)
            {
                if (entry.Command.Definition.Name == "*") wildcardCommands.Remove(scope.PluginId);
                else commands.Remove(entry.Command.Definition.Name);
            }
        }
        foreach (var entry in removed) Cancel(entry);
    }

    private void Cancel(Entry entry)
    {
        try { entry.Cancellation.Cancel(); }
        catch (AggregateException exception) { logger.LogWarning(NotificationFailureDiagnostics.Redact(exception), "插件「{PluginName}」命令取消回调异常，命令：{Name}", entry.Command.PluginDisplayName, entry.Command.Definition.Name); }
        finally { entry.Cancellation.Dispose(); }
    }

    public sealed class Scope : IPluginCommandApi, IDisposable
    {
        private readonly PluginCommandRegistry registry;
        internal string PluginId { get; }
        internal string DisplayName { get; }
        internal Scope(PluginCommandRegistry registry, string pluginId, string displayName)
        {
            this.registry = registry;
            PluginId = pluginId;
            DisplayName = displayName;
        }
        public IDisposable Register(PluginCommandDefinition definition, PluginCommandHandler handler) => registry.Register(this, definition, handler);
        public void Dispose() => registry.Remove(this);
    }

    private sealed record Entry(Scope Scope, RegisteredPluginCommand Command, PluginCommandHandler Handler, long RegistrationOrder)
    {
        internal CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? removal = remove;
        public void Dispose() => Interlocked.Exchange(ref removal, null)?.Invoke();
    }
}
