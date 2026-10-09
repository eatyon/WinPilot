using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Plugins;
using WinPilot.Features.Plugins;

namespace WinPilot.Test;

public sealed class PluginLifecycleConcurrencyTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherPluginsCanStartIndependentlyButReloadWaitsForActiveLifecycle(bool reload)
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "winpilot-lifecycle", Guid.NewGuid().ToString("N")));
        paths.EnsurePluginsDirectory();
        foreach (var id in new[] { "first", "second" })
        {
            var directory = Path.Combine(paths.PluginsDirectory, id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(new { id, name = id, version = "1.0.0", main = "plugin.dll" }));
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            var data = Path.Combine(paths.PluginDataDirectory, id);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "plugin.json"), """{"enabled":true}""");
        }
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var runtime = new RecordingRuntime();
        var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, runtime, pages);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;
        runtime.BeforeStart = plugin =>
        {
            if (plugin.Id == "first" && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            if (plugin.Id == "second") secondEntered.TrySetResult();
        };
        var first = Task.Run(() => manager.Start("first"));
        Task? other = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            other = Task.Run(() => { if (reload) manager.Reload(); else manager.Start("second"); });
            if (reload)
            {
                await Task.Delay(100);
                Assert.False(secondEntered.Task.IsCompleted);
                Assert.False(other.IsCompleted);
            }
            else await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            release.Set();
            await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, runtime.Active);
            Assert.All(manager.GetPlugins(), plugin => Assert.Equal(PluginState.Running, plugin.State));
        }
        finally
        {
            release.Set();
            await first;
            if (other is not null) await other;
            manager.Stop("first");
            manager.Stop("second");
            Directory.Delete(paths.ApplicationDirectory, true);
        }
    }

    [Fact]
    public async Task RapidLifecycleChangesDoNotOverlapOrInvalidateCommandScopes()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "winpilot-lifecycle", Guid.NewGuid().ToString("N")));
        paths.EnsurePluginsDirectory();
        var directory = Path.Combine(paths.PluginsDirectory, "test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            """{"id":"test","name":"Test","version":"1.0.0","main":"plugin.dll"}""");
        File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
        var dataDirectory = Path.Combine(paths.PluginDataDirectory, "test");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "plugin.json"), """{"enabled":true}""");
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var runtime = new RecordingRuntime();
        var logger = new CommandBotTestObserver<PluginManager>();
        var manager = new PluginManager(paths, logger, runtime, pages);
        try
        {
            var operations = Enumerable.Range(0, 80).Select(index => Task.Run(() =>
            {
                switch (index % 5)
                {
                    case 0: manager.Enable("test"); break;
                    case 1: manager.Disable("test"); break;
                    case 2: manager.Restart("test"); break;
                    case 3: manager.Reload(); break;
                    default: manager.Stop("test"); break;
                }
            }));
            await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(15));
            manager.Enable("test");
            Assert.Equal(1, runtime.Active);
            Assert.Equal(1, runtime.MaximumActive);
            Assert.Single(runtime.Registry.GetAll());
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
            manager.Disable("test");
            Assert.Equal(0, runtime.Active);
            Assert.Empty(runtime.Registry.GetAll());
        }
        finally
        {
            manager.Stop("test");
            Directory.Delete(paths.ApplicationDirectory, true);
        }
    }

    private sealed class RecordingRuntime : IPluginRuntime
    {
        internal PluginCommandRegistry Registry { get; } = new(_ => false);
        internal int Active;
        internal int MaximumActive;
        internal Action<PluginInfo>? BeforeStart;

        public IPluginRuntimeInstance Start(PluginInfo plugin)
        {
            BeforeStart?.Invoke(plugin);
            var scope = Registry.CreateScope(plugin.Id, "Test");
            try
            {
                Thread.Sleep(5);
                scope.Register(new($"{plugin.Id}命令", "测试"), (_, _) => Task.CompletedTask);
                var active = Interlocked.Increment(ref Active);
                MaximumActive = Math.Max(MaximumActive, active);
                return new Instance(this, scope);
            }
            catch { scope.Dispose(); throw; }
        }

        private sealed class Instance(RecordingRuntime owner, PluginCommandRegistry.Scope scope) : IPluginRuntimeInstance
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                Thread.Sleep(5);
                scope.Dispose();
                Interlocked.Decrement(ref owner.Active);
            }
        }
    }
}
