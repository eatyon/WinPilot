using WinPilot.Core;
using System.Text.Json;
using WinPilot.Features.Plugins;
using WinPilot.Core.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{
    [Fact]
    public void Disposal_StopsAllPluginsOnceWithoutChangingEnabledSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "first", "first", "");
            WritePlugin(paths, "second", "second", "");
            var settings = new[] { "first", "second" }.ToDictionary(id => id,
                id => File.ReadAllText(Path.Combine(paths.PluginDataDirectory, id, "plugin.json")));
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);
            manager.Dispose();
            manager.Dispose();
            Assert.Equal(new[] { "first", "second" }, runtime.DisposedIds.OrderBy(id => id));
            foreach (var (id, content) in settings)
                Assert.Equal(content, File.ReadAllText(Path.Combine(paths.PluginDataDirectory, id, "plugin.json")));
            Assert.Throws<ObjectDisposedException>(() => manager.Start("first"));
            Assert.Throws<ObjectDisposedException>(() => manager.Reload());
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LifecycleQuery_WaitsWithoutBlockingOrdinaryReadsAndCanBeCancelled(bool failStart, bool reload)
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        using var runtime = new WaitingPluginRuntime(failStart);
        Task? operation = null;
        PluginManager? manager = null;
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var directory = Path.Combine(paths.PluginsDirectory, "example");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), """
                { "id": "example", "name": "Example", "version": "1.0.0", "main": "plugin.dll" }
                """);
            WritePlugin(paths, "other", "other", "");
            manager = CreatePluginManager(paths, runtime);
            if (reload) manager.Enable("example");
            runtime.BlockStart = true;
            operation = Task.Run(() => { if (reload) manager.Reload(); else manager.Enable("example"); });
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(PluginState.Loaded, manager.GetPlugin("example").State);
            if (!reload)
                Assert.Equal(PluginState.Running, (await manager.GetPluginAfterLifecycleAsync("other", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).State);
            var waiting = manager.GetPluginAfterLifecycleAsync("example", CancellationToken.None);
            Assert.False(waiting.IsCompleted);
            using var cancellation = new CancellationTokenSource();
            var cancelled = manager.GetPluginAfterLifecycleAsync("example", cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(operation.IsCompleted);
            Assert.False(waiting.IsCompleted);

            runtime.Release();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            var plugin = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(failStart ? PluginState.Failed : PluginState.Running, plugin.State);
            Assert.True(plugin.Enabled);
            Assert.Equal(plugin, await manager.GetPluginAfterLifecycleAsync("example", CancellationToken.None));
        }
        finally
        {
            runtime.Release();
            if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(5));
            manager?.Disable("example");
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Fact]
    public void Readme_IsOptionalAndReadOnlyWhenRequested()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var directory = Path.Combine(paths.PluginsDirectory, "readme-plugin");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), """
                { "id": "readme-plugin", "name": "Readme Plugin", "version": "1.0.0", "main": "plugin.dll" }
                """);
            var manager = CreatePluginManager(paths, new RecordingPluginRuntime());
            Assert.False(manager.HasReadme("readme-plugin"));
            Assert.Throws<FileNotFoundException>(() => manager.ReadReadme("readme-plugin"));

            var readmePath = Path.Combine(directory, "README.md");
            File.WriteAllText(readmePath, "# 插件说明\n\n测试内容");
            Assert.True(manager.HasReadme("readme-plugin"));
            Assert.Equal("# 插件说明\n\n测试内容", manager.ReadReadme("readme-plugin"));

            File.WriteAllBytes(readmePath, new byte[512 * 1024 + 1]);
            Assert.Throws<InvalidOperationException>(() => manager.ReadReadme("readme-plugin"));
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Fact]
    public void HostAssemblyDoesNotImplementPluginContract()
    {
        var types = typeof(PluginManager).Assembly.GetTypes();
        Assert.DoesNotContain(types, type => typeof(ICSharpPlugin).IsAssignableFrom(type));
    }

    [Fact]
    public void PluginHostContext_ExposesRequiredSecurityContext()
    {
        var security = new FixedPluginSecurity(false);
        var context = new PluginHostContext(null, null, security);

        Assert.Same(security, context.Security);
    }

    [Fact]
    public void Reload_DiscoversOnlyValidPlugins()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var validDirectory = Path.Combine(paths.PluginsDirectory, "example");
            Directory.CreateDirectory(validDirectory);
            File.WriteAllText(Path.Combine(validDirectory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(validDirectory, "manifest.json"), """
                { "id": "example", "name": "Example", "version": "1.0.0", "main": "plugin.dll" }
                """);
            var dataDirectory = Path.Combine(paths.PluginDataDirectory, "example");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "plugin.json"), "{ \"enabled\": true }");
            var invalidDirectory = Path.Combine(paths.PluginsDirectory, "broken");
            Directory.CreateDirectory(invalidDirectory);
            File.WriteAllText(Path.Combine(invalidDirectory, "manifest.json"), "{");

            var runtime = new RecordingPluginRuntime();
            var manager = CreatePluginManager(paths, runtime);
            var plugins = manager.GetPlugins();

            Assert.Single(plugins);
            Assert.Equal(PluginState.Running, Assert.Single(plugins, plugin => plugin.Id == "example").State);
            Assert.Equal(["example"], runtime.StartedIds);
            Assert.DoesNotContain(plugins, plugin => plugin.DirectoryName == "broken");
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Fact]
    public void DisableAndEnable_PersistsPluginStateInPluginData()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var directory = Path.Combine(paths.PluginsDirectory, "example");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), """
                { "id": "example", "name": "Example", "version": "1.0.0", "main": "plugin.dll" }
                """);
            var manager = CreatePluginManager(paths, new RecordingPluginRuntime());

            Assert.Equal(PluginState.Disabled, manager.GetPlugin("example").State);
            manager.Disable("example");
            Assert.Equal(PluginState.Disabled, manager.GetPlugin("example").State);
            Assert.Contains("\"enabled\": false", File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json")));
            manager.Enable("example");

            Assert.Equal(PluginState.Running, manager.GetPlugin("example").State);
            Assert.Contains("\"enabled\": true", File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json")));
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Fact]
    public void Manifest_UsesOnlyNewFieldNamesAndIndependentUiDeclarations()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);

            var current = Path.Combine(paths.PluginsDirectory, "current");
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(current, "custom-settings.json"), "[]");
            File.WriteAllText(Path.Combine(current, "custom-features.json"), "{\"features\":[]}");
            File.WriteAllText(Path.Combine(current, "settings.json"), "invalid fixed-name file must not be read");
            File.WriteAllText(Path.Combine(current, "features.json"), "invalid fixed-name file must not be read");
            File.WriteAllText(Path.Combine(current, "manifest.json"), """
                {
                  "id": "current",
                  "name": "Current",
                  "version": "1.0.0",
                  "winpilot_version": "0.0.0",
                  "status_ui": true,
                  "features_ui": "custom-features.json",
                  "settings_ui": "custom-settings.json",
                  "main": "plugin.dll"
                }
                """);

            var undeclared = Path.Combine(paths.PluginsDirectory, "undeclared");
            Directory.CreateDirectory(undeclared);
            File.WriteAllText(Path.Combine(undeclared, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(undeclared, "settings.json"), "invalid fixed-name file must not be read");
            File.WriteAllText(Path.Combine(undeclared, "features.json"), "invalid fixed-name file must not be read");
            File.WriteAllText(Path.Combine(undeclared, "manifest.json"), """
                { "id": "undeclared", "name": "Undeclared", "version": "1.0.0", "main": "plugin.dll" }
                """);

            var legacy = Path.Combine(paths.PluginsDirectory, "legacy");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(legacy, "manifest.json"), """
                { "id": "legacy", "name": "Legacy", "version": "1.0.0", "entry": "plugin.dll", "minWinPilotVersion": "0.0.0", "capabilities": { "status": true } }
                """);

            var ignoredLegacyFields = Path.Combine(paths.PluginsDirectory, "ignored-legacy-fields");
            Directory.CreateDirectory(ignoredLegacyFields);
            File.WriteAllText(Path.Combine(ignoredLegacyFields, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(ignoredLegacyFields, "manifest.json"), """
                { "id": "ignored-legacy-fields", "name": "Ignored Legacy Fields", "version": "1.0.0", "main": "plugin.dll", "minWinPilotVersion": "999.0.0", "capabilities": { "status": true } }
                """);

            var legacyStatusFile = Path.Combine(paths.PluginsDirectory, "legacy-status-file");
            Directory.CreateDirectory(legacyStatusFile);
            File.WriteAllText(Path.Combine(legacyStatusFile, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(legacyStatusFile, "status.json"), "{}");
            File.WriteAllText(Path.Combine(legacyStatusFile, "manifest.json"), """
                { "id": "legacy-status-file", "name": "Legacy Status File", "version": "1.0.0", "main": "plugin.dll", "status_ui": "status.json" }
                """);

            var manager = CreatePluginManager(paths, new RecordingPluginRuntime());

            Assert.Equal(["current", "ignored-legacy-fields", "undeclared"], manager.GetPlugins().Select(plugin => plugin.Id).Order().ToArray());
            Assert.Equal("[]", manager.ReadSettingsDefinition("current"));
            Assert.Equal("{\"features\":[]}", manager.ReadFeaturesDefinition("current"));
            Assert.Equal("[]", manager.ReadSettingsDefinition("undeclared"));
            Assert.Equal("{\"features\":[]}", manager.ReadFeaturesDefinition("undeclared"));

            var manifest = manager.GetPlugin("current").Manifest!;
            Assert.True(manifest.StatusUi);
            Assert.False(manager.GetPlugin("undeclared").Manifest!.StatusUi);
            Assert.False(manager.GetPlugin("ignored-legacy-fields").Manifest!.StatusUi);
            Assert.Null(manager.GetPlugin("ignored-legacy-fields").Manifest!.WinPilotVersion);
            var serialized = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Contains("\"winpilot_version\"", serialized);
            Assert.Contains("\"status_ui\"", serialized);
            Assert.Contains("\"features_ui\"", serialized);
            Assert.Contains("\"settings_ui\"", serialized);
            Assert.DoesNotContain("minWinPilotVersion", serialized);
            Assert.DoesNotContain("capabilities", serialized);
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Fact]
    public void WriteStorageAndRestart_RestartsOnlyTheEnabledTargetPlugin()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            WritePlugin(paths, "plugin-a", "plugin-a", "");
            WritePlugin(paths, "plugin-b", "plugin-b", "");
            var runtime = new RecordingPluginRuntime();
            var manager = CreatePluginManager(paths, runtime);
            runtime.StartedIds.Clear();
            runtime.DisposedIds.Clear();

            manager.WriteStorage("plugin-a", "{}");
            manager.Restart("plugin-a");

            Assert.Equal(["plugin-a"], runtime.DisposedIds);
            Assert.Equal(["plugin-a"], runtime.StartedIds);
            Assert.Equal(PluginState.Running, manager.GetPlugin("plugin-b").State);

            manager.Disable("plugin-a");
            runtime.StartedIds.Clear();
            runtime.DisposedIds.Clear();
            manager.WriteStorage("plugin-a", "{}");
            manager.Restart("plugin-a");

            Assert.Empty(runtime.DisposedIds);
            Assert.Empty(runtime.StartedIds);
            Assert.Equal(PluginState.Disabled, manager.GetPlugin("plugin-a").State);
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Fact]
    public void Reload_KeepsEnabledPluginWithUnsupportedMinimumVersionInvalidAndStopped()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var directory = Path.Combine(paths.PluginsDirectory, "future-plugin");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), "{ \"id\": \"future-plugin\", \"name\": \"Future Plugin\", \"version\": \"1.0.0\", \"main\": \"plugin.dll\", \"winpilot_version\": \"999.0.0\" }");
            var dataDirectory = Path.Combine(paths.PluginDataDirectory, "future-plugin");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "plugin.json"), "{ \"enabled\": true }");
            var runtime = new RecordingPluginRuntime();
            var manager = CreatePluginManager(paths, runtime);

            var plugin = Assert.Single(manager.GetPlugins());

            Assert.True(plugin.Enabled);
            Assert.Equal(PluginState.Invalid, plugin.State);
            Assert.True(plugin.CanChangeEnabled);
            Assert.Contains("需要 WinPilot 999.0.0 或更高版本", plugin.Errors);
            Assert.Empty(runtime.StartedIds);

            manager.Disable("future-plugin");
            plugin = manager.GetPlugin("future-plugin");
            Assert.False(plugin.Enabled);
            Assert.Equal(PluginState.Invalid, plugin.State);
            using (var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "plugin.json")))) Assert.False(lifecycle.RootElement.GetProperty("enabled").GetBoolean());

            manager.Enable("future-plugin");
            plugin = manager.GetPlugin("future-plugin");
            Assert.True(plugin.Enabled);
            Assert.Equal(PluginState.Invalid, plugin.State);
            using (var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "plugin.json")))) Assert.True(lifecycle.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Empty(runtime.StartedIds);

            manager.Reload();
            plugin = Assert.Single(manager.GetPlugins());
            Assert.True(plugin.Enabled);
            Assert.Equal(PluginState.Invalid, plugin.State);
            Assert.Empty(runtime.StartedIds);
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Fact]
    public void PluginManager_ReportsStatusSubscriberLifecycleToRuntime()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            WritePlugin(paths, "status-tracker", "status-tracker", "");
            var runtime = new StatusSubscriberTrackingRuntime();
            var manager = CreatePluginManager(paths, runtime);

            using var first = manager.SubscribeState("status-tracker", _ => { });
            using var second = manager.SubscribeState("status-tracker", _ => { });
            Assert.Equal([0, 1, 2], runtime.Instances.Single().Counts);

            second.Dispose();
            Assert.Equal([0, 1, 2, 1], runtime.Instances.Single().Counts);
            manager.Reload();
            Assert.Equal(2, runtime.Instances.Count);
            Assert.Equal([1], runtime.Instances[1].Counts);
            first.Dispose();
            Assert.Equal([1, 0], runtime.Instances[1].Counts);
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

}
