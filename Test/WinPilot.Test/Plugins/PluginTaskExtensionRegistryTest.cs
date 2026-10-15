using WinPilot.Core;
using System.Text.Json;
using System.Collections;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class PluginTaskExtensionRegistryTest
{
    [Fact]
    public async Task BarrierGenerationReplacementNeverPairsOldEventWithNewToken()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        PluginTaskTriggerFired? received = null;
        registry.Triggered += fired => received = fired;
        for (var round = 0; round < 10000; round++)
        {
            using var previous = registry.CreateScope("test", "Test");
            previous.Register([new("trigger", "Trigger")]);
            var trigger = new PluginTriggerEvent("trigger", DateTimeOffset.UtcNow, new Dictionary<string, object?>());
            previous.Emit(trigger);
            Assert.NotNull(received);
            var original = received;
            received = null;
            using var start = new Barrier(2);
            PluginTaskExtensionScope? current = null;
            try
            {
                await Task.WhenAll(Task.Run(() =>
                {
                    Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
                    previous.Emit(trigger);
                }), Task.Run(() =>
                {
                    Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
                    current = registry.CreateScope("test", "Test");
                })).WaitAsync(TimeSpan.FromSeconds(10));
                if (received is not null)
                {
                    Assert.Equal(original.Generation, received.Generation);
                    Assert.Equal(original.CancellationToken, received.CancellationToken);
                }
                Assert.True(original.CancellationToken.IsCancellationRequested);
            }
            finally { current?.Dispose(); }
        }
    }

    [Fact]
    public async Task EventDelayedDuringPayloadSnapshotIsRejectedAfterGenerationReplacement()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var previous = registry.CreateScope("test", "Test");
        previous.Register([new("trigger", "Trigger")]);
        var received = new List<PluginTaskTriggerFired>();
        registry.Triggered += received.Add;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var snapshot = new TriggerSnapshotHandshake(entered, release);
        var pending = Task.Run(() => previous.Emit(new("trigger", DateTimeOffset.UtcNow, snapshot)));
        PluginTaskExtensionScope? current = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            current = registry.CreateScope("test", "Test");
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(snapshot.Error);
            Assert.Empty(received);
            previous.Dispose();
            current.Register([new("trigger", "Current trigger")]);
            current.Emit(new("trigger", DateTimeOffset.UtcNow, new Dictionary<string, object?>()));
            Assert.Single(received);
            Assert.False(received[0].CancellationToken.IsCancellationRequested);
        }
        finally { release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); current?.Dispose(); }
    }

    private sealed class TriggerSnapshotHandshake(TaskCompletionSource entered, ManualResetEventSlim release) : IReadOnlyDictionary<string, object?>
    {
        internal Exception? Error { get; private set; }
        public int Count => 1;
        public IEnumerable<string> Keys => ["value"];
        public IEnumerable<object?> Values => ["old"];
        public object? this[string key] => key == "value" ? "old" : throw new KeyNotFoundException();
        public bool ContainsKey(string key) => key == "value";
        public bool TryGetValue(string key, out object? value) { value = key == "value" ? "old" : null; return key == "value"; }
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { Error = new TimeoutException("事件快照握手未解除"); throw Error; }
            yield return new("value", "old");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingCancellationCallbackDoesNotPreventGenerationCleanup(bool replaceGeneration)
    {
        var logger = new CancellationLogger();
        var registry = new PluginTaskExtensionRegistry(new(logger), new(), new());
        using var old = registry.CreateScope("test-plugin", "Test");
        old.Register([new("trigger", "Trigger")]);
        ((IPluginNotificationVariableApi)old).Register([new("$test.value", "Value")]);
        ((IPluginTaskActionApi)old).Register([new("action", "Action")]);
        CancellationToken token = default;
        registry.Triggered += trigger => token = trigger.CancellationToken;
        old.Emit(new("trigger", DateTimeOffset.UtcNow, new Dictionary<string, object?>()));
        var failure = new InvalidOperationException("取消回调失败");
        using var callback = token.Register(() => throw failure);
        PluginTaskExtensionScope? current = null;
        try
        {
            if (replaceGeneration)
            {
                current = registry.CreateScope("test-plugin", "Test");
                old.Dispose();
                current.Register([new("trigger", "New trigger")]);
                ((IPluginNotificationVariableApi)current).Register([new("$test.value", "New value")]);
                ((IPluginTaskActionApi)current).Register([new("action", "New action")]);
                old.Dispose();
                Assert.Equal("New trigger", Assert.Single(registry.GetTriggers()).TriggerName);
                Assert.Equal("New value", Assert.Single(registry.GetVariables()).DisplayName);
                Assert.Equal("New action", Assert.Single(registry.GetActions()).ActionName);
            }
            else
            {
                old.Dispose();
                old.Dispose();
                Assert.Empty(registry.GetTriggers());
                Assert.Empty(registry.GetVariables());
                Assert.Empty(registry.GetActions());
                current = registry.CreateScope("test-plugin", "Test");
                current.Register([new("trigger", "New trigger")]);
                Assert.Single(registry.GetTriggers());
            }
            Assert.True(token.IsCancellationRequested);
            var warning = Assert.Single(logger.Errors);
            Assert.Same(failure, Assert.Single(Assert.IsType<AggregateException>(warning).InnerExceptions));
        }
        finally { current?.Dispose(); }
        Assert.Empty(registry.GetTriggers());
        Assert.Empty(registry.GetVariables());
        Assert.Empty(registry.GetActions());
    }

    private sealed class CancellationLogger : Microsoft.Extensions.Logging.ILogger<PluginTaskTriggerRegistry>
    {
        internal List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, level);
            Assert.Contains("test-plugin", formatter(state, exception));
            Errors.Add(exception);
        }
    }

    [Fact]
    public void ScopeBindsPluginAndGenerationAndDisposesIdempotently()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("plugin-a", "Plugin A");
        var received = 0;
        registry.Triggered += _ => received++;
        var triggers = registry.GetTriggers();
        Assert.Empty(triggers);
        using var registration = scope.Register([new PluginTriggerDefinition("trigger-a", "Trigger A")]);
        Assert.Equal(("plugin-a", "Plugin A", "trigger-a"), registry.GetTriggers().Select(item => (item.PluginId, item.PluginDisplayName, item.TriggerId)).Single());
        scope.Emit(new PluginTriggerEvent("trigger-a", DateTimeOffset.UtcNow, new Dictionary<string, object?> { ["value"] = "current" }));
        Assert.Equal(1, received);
        registration.Dispose();
        registration.Dispose();
        Assert.Empty(registry.GetTriggers());
        scope.Dispose();
        scope.Dispose();
        scope.Emit(new PluginTriggerEvent("trigger-a", DateTimeOffset.UtcNow, new Dictionary<string, object?>()));
        Assert.Equal(1, received);
    }

    [Fact]
    public void RegistriesRejectDuplicatesAndDoNotStoreVariableValues()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("plugin-a", "Plugin A");
        using var first = ((IPluginNotificationVariableApi)scope).Register([new PluginVariableDefinition("$plugin.value", "Value")]);
        Assert.Throws<InvalidOperationException>(() => ((IPluginNotificationVariableApi)scope).Register([new PluginVariableDefinition("$plugin.value", "Duplicate")]));
        using var action = ((IPluginTaskActionApi)scope).Register([new PluginTaskActionDefinition("action-a", "Action A")]);
        Assert.Single(registry.GetVariables());
        Assert.DoesNotContain("current", JsonSerializer.Serialize(registry.GetVariables()));
        Assert.Single(registry.GetActions());
    }

    [Fact]
    public void TriggerBatchValidationFailureDoesNotPartiallyRegister()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("plugin-a", "Plugin A");

        Assert.Throws<ArgumentException>(() => scope.Register(
        [
            new PluginTriggerDefinition("trigger-a", "Trigger A"),
            new PluginTriggerDefinition("trigger-b", "Trigger B",
                [new PluginActionParameterDefinition("", "Invalid parameter", PluginActionParameterType.Text)])
        ]));

        Assert.Empty(registry.GetTriggers());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActionBatchValidationFailureDoesNotPartiallyRegister(bool invalidPlan)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("plugin-a", "Plugin A");
        var actions = (IPluginTaskActionApi)scope;
        PluginActionParameterDefinition[]? parameters = invalidPlan ? null
            : [new PluginActionParameterDefinition("", "Invalid parameter", PluginActionParameterType.Text)];
        var plan = invalidPlan ? new PluginHostActionPlan([]) : null;

        Assert.Throws<ArgumentException>(() => actions.Register(
        [
            new PluginTaskActionDefinition("action-a", "Action A"),
            new PluginTaskActionDefinition("action-b", "Action B", parameters, plan)
        ]));

        Assert.Empty(registry.GetActions());
    }

    [Theory]
    [InlineData("trigger-a")]
    [InlineData("TRIGGER-A")]
    public void TriggerBatchRejectsDuplicateIds(string duplicateId)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("plugin-a", "Plugin A");

        Assert.Throws<InvalidOperationException>(() => scope.Register(
        [
            new PluginTriggerDefinition("trigger-a", "Trigger A"),
            new PluginTriggerDefinition(duplicateId, "Duplicate trigger")
        ]));

        Assert.Empty(registry.GetTriggers());
    }

    [Theory]
    [InlineData("action-a")]
    [InlineData("ACTION-A")]
    public void ActionBatchRejectsDuplicateIds(string duplicateId)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("plugin-a", "Plugin A");
        var actions = (IPluginTaskActionApi)scope;

        Assert.Throws<InvalidOperationException>(() => actions.Register(
        [
            new PluginTaskActionDefinition("action-a", "Action A"),
            new PluginTaskActionDefinition(duplicateId, "Duplicate action")
        ]));

        Assert.Empty(registry.GetActions());
    }

    [Fact]
    public async Task DynamicVariableResolver_RunsOnlyForRegisteredRequestedVariables()
    {
        var calls = 0;
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        ((IPluginNotificationVariableApi)scope).Register([new("$test.summary", "Summary")], (names, _, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["$test.summary"] = "resolved"
            });
        });
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now, new Dictionary<string, object?>());

        Assert.Empty(await registry.ResolveVariablesAsync(["$other.value"], context, CancellationToken.None));
        var result = await registry.ResolveVariablesAsync(["$test.summary"], context, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("resolved", result["$test.summary"]);
        scope.Dispose();
        Assert.Empty(await registry.ResolveVariablesAsync(["$test.summary"], context, CancellationToken.None));
    }

    [Fact]
    public async Task ImageVariableRegistrationRejectsTextResult()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        ((IPluginNotificationVariableApi)scope).Register(
            [new PluginVariableDefinition("&test.image", "Image", Type: PluginVariableType.Image)],
            (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["&test.image"] = "not-an-image" }));
        Assert.Equal(PluginVariableType.Image, Assert.Single(registry.GetVariables()).Type);

        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now, new Dictionary<string, object?>());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.ResolveVariablesAsync(["&test.image"], context, CancellationToken.None));

        Assert.Equal("图片变量 &test.image 返回了无效内容", exception.Message);
    }

    [Theory]
    [InlineData("replace-all")]
    [InlineData("replace-one")]
    [InlineData("remove")]
    public async Task LateVariableResultsAreAcceptedOnlyForUnchangedRegistrations(string operation)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test", "Test");
        var variables = (IPluginNotificationVariableApi)scope;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        PluginNotificationVariableResolver resolver = async (names, _, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) { entered.TrySetResult(); await release.Task; }
            return names.ToDictionary(name => name, name => (object?)$"{(call == 1 ? "old" : "new")}:{name}", StringComparer.OrdinalIgnoreCase);
        };
        using var first = variables.Register([new("$test.first", "First")], resolver);
        using var second = variables.Register([new("$test.second", "Second")], resolver);
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now, new Dictionary<string, object?>());
        var pending = registry.ResolveVariablesAsync(["$test.first", "$test.second"], context, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first.Dispose();
            if (operation != "replace-one") second.Dispose();
            if (operation != "remove") variables.Register([new("$test.first", "First")], resolver);
            if (operation == "replace-all") variables.Register([new("$test.second", "Second")], resolver);
            release.TrySetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, calls);
            if (operation == "replace-one")
            {
                Assert.Single(result);
                Assert.Equal("old:$test.second", result["$test.second"]);
                Assert.False(result.ContainsKey("$test.first"));
            }
            else Assert.Empty(result);
            var current = await registry.ResolveVariablesAsync(["$test.first", "$test.second"], context, CancellationToken.None);
            if (operation == "remove")
            {
                Assert.Empty(current);
                Assert.Equal(1, calls);
            }
            else
            {
                Assert.Equal(2, calls);
                Assert.Equal(2, current.Count);
                Assert.Equal("new:$test.first", current["$test.first"]);
                Assert.Equal("new:$test.second", current["$test.second"]);
            }
        }
        finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task EqualResolverRegistrationsKeepOneBatchAndAcceptEveryName()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test", "Test");
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now, new Dictionary<string, object?>());
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        PluginNotificationVariableResolver resolver = (names, receivedContext, token) =>
        {
            calls++;
            Assert.Equal(new[] { "$test.first", "$test.second", "$test.third" }, names);
            Assert.Same(context, receivedContext);
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(names.ToDictionary(name => name, name => (object?)name));
        };
        using var first = ((IPluginNotificationVariableApi)scope).Register([new("$test.first", "First")], resolver);
        using var rest = ((IPluginNotificationVariableApi)scope).Register([new("$test.second", "Second"), new("$test.third", "Third")], resolver);
        var result = await registry.ResolveVariablesAsync(["$test.first", "$TEST.FIRST", "$test.second", "$test.third", "$test.missing"], context, cancellation.Token);
        Assert.Equal(1, calls);
        Assert.Equal(3, result.Count);
        foreach (var name in new[] { "$test.first", "$test.second", "$test.third" }) Assert.Equal(name, result[name]);
    }

    [Fact]
    public void ScopeDispose_CancelsGenerationAndRemovesRegistrations()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        scope.Register([new("test-trigger", "Test trigger")]);
        Assert.Single(registry.GetTriggers());
        scope.Dispose();
        Assert.Empty(registry.GetTriggers());
    }

    [Fact]
    public async Task ActionRegistration_ExposesHandlerAndReadonlyContext()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        var parameters = new Dictionary<string, object?> { ["path"] = "app.exe" };
        var variables = new Dictionary<string, object?> { ["$test.value"] = "current" };
        using var cancellation = new CancellationTokenSource();
        using var triggerCancellation = new CancellationTokenSource();
        var context = new PluginTaskRunContext("task", "test-plugin", "test-trigger",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), variables, triggerCancellation.Token);
        var calls = 0;
        var receivedToken = CancellationToken.None;
        ((IPluginTaskActionApi)scope).Register([new("test-action", "Test action")], (receivedParameters, receivedContext, token) =>
        {
            calls++;
            Assert.Same(parameters, receivedParameters);
            Assert.Equal("app.exe", receivedParameters["path"]);
            Assert.Same(context, receivedContext);
            Assert.Same(variables, receivedContext.Variables);
            Assert.Equal("current", receivedContext.Variables["$test.value"]);
            Assert.Equal(triggerCancellation.Token, receivedContext.CancellationToken);
            Assert.Equal(cancellation.Token, token);
            Assert.NotEqual(receivedContext.CancellationToken, token);
            receivedToken = token;
            return Task.FromResult(PluginTaskActionResult.Success());
        });
        var action = Assert.Single(registry.GetActions());
        var result = await action.Handler!(parameters, context, cancellation.Token);
        Assert.Equal(PluginTaskActionResultStatus.Success, result.Status);
        Assert.Equal(1, calls);
        cancellation.Cancel();
        Assert.True(receivedToken.IsCancellationRequested);
        Assert.False(context.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void HostActionPlan_RegistersFlatAllowlistedSteps()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        var plan = new PluginHostActionPlan([new(PluginHostActionType.Delay, new Dictionary<string, object?> { ["delaySeconds"] = 1 })]);
        ((IPluginTaskActionApi)scope).Register([new("chain", "Chain", HostActionPlan: plan)]);
        Assert.Same(plan, Assert.Single(registry.GetActions()).HostActionPlan);
    }

    [Fact]
    public void HostActionPlan_RejectsEmptyPlan()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        Assert.Throws<ArgumentException>(() => ((IPluginTaskActionApi)scope).Register([new("chain", "Chain", HostActionPlan: new PluginHostActionPlan([]))]));
    }
}
