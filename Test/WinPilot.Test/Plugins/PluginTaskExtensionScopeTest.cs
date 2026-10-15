using System.Collections;
using System.Reflection;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class PluginTaskExtensionScopeTest
{
    [Fact]
    public async Task TriggerCallbackReleasesScopeLockBeforeConcurrentActionRegistration()
    {
        var actions = new PluginTaskActionRegistry();
        var registry = new PluginTaskExtensionRegistry(new(), new(), actions);
        using var scope = registry.CreateScope("test", "Test");
        using var trigger = scope.Register([new("trigger", "Trigger")]);
        var scopeGate = typeof(PluginTaskExtensionScope).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!;
        using var callbackEntered = new ManualResetEventSlim();
        using var validating = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var scopeAvailable = false;
        Exception? callbackError = null;
        Task? registration = null;
        registry.Triggered += _ =>
        {
            try
            {
                callbackEntered.Set();
                Assert.True(validating.Wait(TimeSpan.FromSeconds(5)));
                using var checkedScope = new ManualResetEventSlim();
                var checking = new Thread(() =>
                {
                    scopeAvailable = Monitor.TryEnter(scopeGate);
                    if (scopeAvailable) Monitor.Exit(scopeGate);
                    checkedScope.Set();
                }) { IsBackground = true };
                checking.Start();
                Assert.True(checkedScope.Wait(TimeSpan.FromSeconds(5)));
                checking.Join();
            }
            catch (Exception exception) { callbackError = exception; }
            finally { release.Set(); }
        };
        var emitting = Task.Run(() => scope.Emit(new("trigger", DateTimeOffset.Now, new Dictionary<string, object?>())));
        try
        {
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
            registration = Task.Run(() =>
            {
                using var current = ((IPluginTaskActionApi)scope).Register([new("action", "Action", new RegistrationHandshake(validating, release))]);
            });
            await Task.WhenAll(emitting, registration).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(callbackError);
            Assert.True(scopeAvailable, "触发回调仍持有作用域锁，动作注册持注册表锁后无法取得作用域锁");
            Assert.Empty(registry.GetActions());
        }
        finally
        {
            release.Set();
            await Task.WhenAll(emitting, registration ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void DisposedScopeIgnoresTriggerAndNormalScopeStillEmits()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test", "Test");
        scope.Register([new("trigger", "Trigger")]);
        var received = new List<PluginTaskTriggerFired>();
        registry.Triggered += received.Add;
        var trigger = new PluginTriggerEvent("trigger", DateTimeOffset.Now, new Dictionary<string, object?>());
        scope.Emit(trigger);
        Assert.Single(received);
        Assert.False(received[0].CancellationToken.IsCancellationRequested);
        scope.Dispose();
        scope.Emit(trigger);
        Assert.Single(received);
        Assert.True(received[0].CancellationToken.IsCancellationRequested);
    }

    private sealed class RegistrationHandshake(ManualResetEventSlim entered, ManualResetEventSlim release) : IReadOnlyList<PluginActionParameterDefinition>
    {
        public int Count => 0;
        public PluginActionParameterDefinition this[int index] => throw new IndexOutOfRangeException();
        public IEnumerator<PluginActionParameterDefinition> GetEnumerator()
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            yield break;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("trigger")]
    [InlineData("variable")]
    [InlineData("action")]
    public void RepeatedRegistrationDisposalDoesNotRetainTrackedHandles(string kind)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test", "Test");
        for (var index = 0; index < 200; index++)
        {
            var registration = Register(scope, kind);
            Assert.Single(Tracked(scope).Cast<object>());
            registration.Dispose();
            registration.Dispose();
            Assert.Empty(Tracked(scope).Cast<object>());
            Assert.Empty(registry.GetTriggers());
            Assert.Empty(registry.GetVariables());
            Assert.Empty(registry.GetActions());
        }
    }

    [Fact]
    public void TrackingHandleUnregistersOnceClearsReferencesAndPreservesReverseDisposal()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test", "Test");
        var order = new List<int>();
        var gate = typeof(PluginTaskExtensionScope).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!;
        var originals = Enumerable.Range(1, 3).Select(index => new Registration(() =>
        {
            Assert.False(Monitor.IsEntered(gate));
            order.Add(index);
        })).ToArray();
        var tracked = originals.Select(original => Add(scope, original)).ToArray();
        tracked[1].Dispose();
        tracked[1].Dispose();
        Assert.Equal([2], order);
        Assert.Equal(2, Tracked(scope).Count);
        scope.Dispose();
        scope.Dispose();
        Assert.Equal([2, 3, 1], order);
        Assert.All(originals, original => Assert.Equal(1, original.DisposeCount));
        Assert.Empty(Tracked(scope).Cast<object>());
        Assert.All(tracked, handle => Assert.All(handle.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => Assert.Null(field.GetValue(handle))));
    }

    [Fact]
    public void FailedUnregistrationStillUntracksAndClearsReferences()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test", "Test");
        var failure = new InvalidOperationException("测试注销失败");
        var original = new Registration(() => throw failure);
        var tracked = Add(scope, original);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(tracked.Dispose));
        tracked.Dispose();
        Assert.Equal(1, original.DisposeCount);
        Assert.Empty(Tracked(scope).Cast<object>());
        Assert.All(tracked.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field => Assert.Null(field.GetValue(tracked)));
    }

    [Fact]
    public async Task HandleAndScopeDisposalRaceUnregistersOnce()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        for (var iteration = 0; iteration < 50; iteration++)
        {
            using var scope = registry.CreateScope("test", "Test");
            var original = new Registration(() => { });
            var tracked = Add(scope, original);
            using var start = new Barrier(2);
            await Task.WhenAll(Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); tracked.Dispose(); }),
                Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); scope.Dispose(); })).WaitAsync(TimeSpan.FromSeconds(10));
            tracked.Dispose();
            Assert.Equal(1, original.DisposeCount);
            Assert.Empty(Tracked(scope).Cast<object>());
        }
    }

    [Fact]
    public async Task RegistrationRacingScopeDisposalLeavesNoDefinitionsOrTracking()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        for (var iteration = 0; iteration < 50; iteration++)
        {
            using var scope = registry.CreateScope("test", "Test");
            using var start = new Barrier(2);
            IDisposable? registration = null;
            await Task.WhenAll(Task.Run(() =>
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
                try { registration = Register(scope, "action"); }
                catch (InvalidOperationException exception) { Assert.True(exception is ObjectDisposedException || exception.Message == "插件注册已失效"); }
            }), Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); scope.Dispose(); })).WaitAsync(TimeSpan.FromSeconds(10));
            registration?.Dispose();
            Assert.Empty(Tracked(scope).Cast<object>());
            Assert.Empty(registry.GetActions());
        }
    }

    [Fact]
    public async Task RegistrationCreatedBeforeScopeDisposalIsReleasedWhenTrackingIsRejected()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test", "Test");
        var original = new Registration(() => { });
        var gate = typeof(PluginTaskExtensionScope).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!;
        using var entering = new ManualResetEventSlim();
        Task registering;
        lock (gate)
        {
            registering = Task.Run(() =>
            {
                entering.Set();
                var error = Assert.Throws<TargetInvocationException>(() => Add(scope, original));
                Assert.IsType<ObjectDisposedException>(error.InnerException);
            });
            Assert.True(entering.Wait(TimeSpan.FromSeconds(5)));
            scope.Dispose();
        }
        await registering.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, original.DisposeCount);
        Assert.Empty(Tracked(scope).Cast<object>());
    }

    [Theory]
    [InlineData("trigger")]
    [InlineData("variable")]
    [InlineData("action")]
    public void OldHandleDoesNotRemoveNewGenerationRegistration(string kind)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var previous = registry.CreateScope("test", "Test");
        var old = Register(previous, kind);
        using var current = registry.CreateScope("test", "Test");
        previous.Dispose();
        using var registration = Register(current, kind);
        old.Dispose();
        Assert.Single(Tracked(current).Cast<object>());
        Assert.Equal(kind == "trigger" ? 1 : 0, registry.GetTriggers().Count);
        Assert.Equal(kind == "variable" ? 1 : 0, registry.GetVariables().Count);
        Assert.Equal(kind == "action" ? 1 : 0, registry.GetActions().Count);
    }

    private static IDisposable Register(PluginTaskExtensionScope scope, string kind) => kind switch
    {
        "trigger" => scope.Register([new("trigger", "Trigger")]),
        "variable" => ((IPluginNotificationVariableApi)scope).Register([new("$test.value", "Value")]),
        "action" => ((IPluginTaskActionApi)scope).Register([new("action", "Action")]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static IList Tracked(PluginTaskExtensionScope scope) =>
        (IList)typeof(PluginTaskExtensionScope).GetField("registrations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!;

    private static IDisposable Add(PluginTaskExtensionScope scope, IDisposable registration) =>
        (IDisposable)typeof(PluginTaskExtensionScope).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scope, [registration])!;

    private sealed class Registration(Action dispose) : IDisposable
    {
        private int disposeCount;
        internal int DisposeCount => Volatile.Read(ref disposeCount);
        public void Dispose() { Interlocked.Increment(ref disposeCount); dispose(); }
    }
}
