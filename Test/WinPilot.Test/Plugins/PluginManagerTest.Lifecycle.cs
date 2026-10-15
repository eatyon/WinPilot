using WinPilot.Core;
using System.Text.Json;
using WinPilot.Features.Plugins;
using WinPilot.Core.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartingPluginReadsLatestManifestWithoutScanning(bool restart)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);
            if (!restart) manager.Disable("example");
            File.WriteAllText(Path.Combine(paths.PluginsDirectory, "example", "manifest.json"), """
                { "id": "example", "name": "新版插件", "version": "2.0.0", "main": "new.dll" }
                """);
            File.WriteAllText(Path.Combine(paths.PluginsDirectory, "example", "new.dll"), "");
            runtime.StartedPlugins.Clear();

            if (restart) manager.Restart("example");
            else manager.Enable("example");

            var started = Assert.Single(runtime.StartedPlugins);
            Assert.Equal("新版插件", started.Manifest!.Name);
            Assert.Equal("2.0.0", started.Manifest.Version);
            Assert.Equal("new.dll", started.Manifest.Main);
            Assert.Equal(PluginState.Running, manager.GetPlugin("example").State);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("{\"id\":\"other\",\"name\":\"Other\",\"version\":\"1.0.0\",\"main\":\"plugin.dll\"}")]
    [InlineData("{\"id\":\"example\",\"name\":\"Example\",\"version\":\"1.0.0\",\"main\":\"missing.dll\"}")]
    [InlineData("{\"id\":\"example\",\"name\":\"Example\",\"version\":\"1.0.0\",\"main\":\"plugin.dll\",\"features_ui\":\"missing.json\"}")]
    [InlineData("{\"id\":\"example\",\"name\":\"Example\",\"version\":\"1.0.0\",\"main\":\"plugin.dll\",\"winpilot_version\":\"999.0.0\"}")]
    public void EnableRejectsChangedIdAndInvalidFilesBeforeStarting(string manifest)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);
            manager.Disable("example");
            File.WriteAllText(Path.Combine(paths.PluginsDirectory, "example", "manifest.json"), manifest);
            runtime.StartedIds.Clear();

            manager.Enable("example");

            Assert.Empty(runtime.StartedIds);
            var failed = manager.GetPlugin("example");
            Assert.Equal(PluginState.Failed, failed.State);
            Assert.NotEmpty(failed.Errors);
            Assert.False(failed.Enabled);
            using (var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json"))))
                Assert.False(lifecycle.RootElement.GetProperty("enabled").GetBoolean());
            if (manifest.Contains("winpilot_version"))
                Assert.Contains(failed.Errors, error => error.Contains("需要 WinPilot 999.0.0 或更高版本"));
            Assert.False(Directory.Exists(Path.Combine(paths.PluginDataDirectory, "other")));
            WritePlugin(paths, "example", "example", "");
            manager.Scan();
            manager.Enable("example");
            Assert.Equal(["example"], runtime.StartedIds);
            Assert.Equal(PluginState.Running, manager.GetPlugin("example").State);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_RefreshesInactivePluginMetadataWithoutStartingIt(bool enabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);
            if (enabled) manager.Stop("example");
            else manager.Disable("example");
            var original = manager.GetPlugin("example");
            File.WriteAllText(Path.Combine(original.Directory, "manifest.json"), """
                { "id": "example", "name": "新版插件", "version": "2.0.0", "main": "plugin.dll", "features_ui": "features.json" }
                """);
            File.WriteAllText(Path.Combine(original.Directory, "features.json"), """
                { "features": [{ "id": "refresh", "name": "刷新", "mode": "action" }] }
                """);
            runtime.StartedIds.Clear();
            runtime.DisposedIds.Clear();

            manager.Scan();

            var refreshed = Assert.Single(manager.GetPlugins());
            Assert.Equal(original.Directory, refreshed.Directory);
            Assert.Equal("新版插件", refreshed.Manifest!.Name);
            Assert.Equal("2.0.0", refreshed.Manifest.Version);
            Assert.Equal("features.json", refreshed.Manifest.FeaturesUi);
            Assert.Equal(enabled ? PluginState.Stopped : PluginState.Disabled, refreshed.State);
            Assert.Empty(runtime.StartedIds);
            Assert.Empty(runtime.DisposedIds);

            File.WriteAllText(Path.Combine(original.Directory, "features.json"), "invalid");
            manager.Scan();
            Assert.Empty(manager.GetPlugins());
            Assert.Empty(runtime.StartedIds);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public void Scan_OnlyStartsNewEnabledPluginsAndStopsRemovedPlugins()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "existing", "existing", "");
            WritePlugin(paths, "removed", "removed", "");
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);
            var existing = manager.GetPlugin("existing");
            File.WriteAllText(Path.Combine(existing.Directory, "manifest.json"), "invalid");
            WritePlugin(paths, "new", "new", "");
            WritePlugin(paths, "disabled", "disabled", "");
            File.WriteAllText(Path.Combine(paths.PluginDataDirectory, "disabled", "plugin.json"), "{ \"enabled\": false }");
            Directory.Delete(Path.Combine(paths.PluginsDirectory, "removed"), true);
            runtime.StartedIds.Clear();
            runtime.DisposedIds.Clear();

            manager.Scan();

            Assert.Same(existing, manager.GetPlugin("existing"));
            Assert.Equal(["new"], runtime.StartedIds);
            Assert.Equal(["removed"], runtime.DisposedIds);
            Assert.Equal(PluginState.Running, manager.GetPlugin("new").State);
            Assert.Equal(PluginState.Disabled, manager.GetPlugin("disabled").State);
            Assert.Throws<FileNotFoundException>(() => manager.GetPlugin("removed"));
            Assert.True(File.Exists(Path.Combine(paths.PluginDataDirectory, "removed", "plugin.json")));
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public void Scan_KeepsOriginalForDuplicateIdAndReplacesItAfterDirectoryRemoval()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "original", "example", "");
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);
            var original = manager.GetPlugin("example");
            WritePlugin(paths, "replacement", "example", "");
            runtime.StartedIds.Clear();
            runtime.DisposedIds.Clear();

            manager.Scan();

            Assert.Same(original, Assert.Single(manager.GetPlugins()));
            Assert.Empty(runtime.StartedIds);
            Assert.Empty(runtime.DisposedIds);
            Directory.Delete(original.Directory, true);

            manager.Scan();

            Assert.Equal("replacement", Assert.Single(manager.GetPlugins()).DirectoryName);
            Assert.Equal(PluginState.Running, manager.GetPlugin("example").State);
            Assert.Equal(["example"], runtime.DisposedIds);
            Assert.Equal(["example"], runtime.StartedIds);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_InvalidDuplicateCannotDisableOriginalPlugin(bool running)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "original", "example", "");
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);
            if (!running) manager.Stop("example");
            WritePlugin(paths, "duplicate", "example", "");
            File.WriteAllText(Path.Combine(paths.PluginsDirectory, "duplicate", "manifest.json"), """
                { "id": "example", "name": "重复插件", "version": "1.0.0", "main": "plugin.dll", "winpilot_version": "999.0.0" }
                """);

            manager.Scan();

            var plugin = Assert.Single(manager.GetPlugins());
            Assert.Equal("original", plugin.DirectoryName);
            Assert.True(plugin.Enabled);
            Assert.Equal(running ? PluginState.Running : PluginState.Stopped, plugin.State);
            Assert.Single(runtime.StartedIds);
            using var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json")));
            Assert.True(lifecycle.RootElement.GetProperty("enabled").GetBoolean());
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public void Scan_ScanFailurePreservesCurrentInstancesAndList()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var runtime = new RecordingPluginRuntime();
            var logger = new CommandBotTestObserver<PluginManager>();
            using var manager = CreatePluginManager(paths, runtime, logger);
            var plugin = manager.GetPlugin("example");
            Directory.Move(paths.PluginsDirectory, paths.PluginsDirectory + "-unavailable");
            File.WriteAllText(paths.PluginsDirectory, "blocked");
            runtime.StartedIds.Clear();
            runtime.DisposedIds.Clear();

            manager.Scan();

            Assert.Same(plugin, Assert.Single(manager.GetPlugins()));
            Assert.Empty(runtime.StartedIds);
            Assert.Empty(runtime.DisposedIds);
            Assert.Contains(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error && entry.Message == "扫描插件目录失败");
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("missing.dll", null, null)]
    [InlineData("plugin.dll", "missing.json", null)]
    [InlineData("plugin.dll", null, "999.0.0")]
    public void InitialScan_InvalidDuplicateCannotChangeSharedEnabledSetting(string main, string? featuresUi, string? minimumVersion)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            foreach (var name in new[] { "valid", "invalid" })
                WritePlugin(paths, name, "example", "");
            File.WriteAllText(Path.Combine(paths.PluginsDirectory, "invalid", "manifest.json"),
                JsonSerializer.Serialize(new { id = "example", name = "Invalid", version = "1.0.0", main, features_ui = featuresUi, winpilot_version = minimumVersion }));
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);

            if (minimumVersion is null)
            {
                var plugin = Assert.Single(manager.GetPlugins());
                Assert.Equal("valid", plugin.DirectoryName);
                Assert.True(plugin.Enabled);
                Assert.Equal(PluginState.Running, plugin.State);
                Assert.Equal(["example"], runtime.StartedIds);
            }
            else
            {
                Assert.Empty(manager.GetPlugins());
                Assert.Empty(runtime.StartedIds);
            }
            using var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json")));
            Assert.True(lifecycle.RootElement.GetProperty("enabled").GetBoolean());
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public void InitialScan_RejectedDuplicatesCannotChangeSharedEnabledSetting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            foreach (var name in new[] { "first", "second" })
            {
                WritePlugin(paths, name, "example", "");
                File.Delete(Path.Combine(paths.PluginsDirectory, name, "plugin.dll"));
            }
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);

            Assert.Empty(manager.GetPlugins());
            Assert.Empty(runtime.StartedIds);
            using var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json")));
            Assert.True(lifecycle.RootElement.GetProperty("enabled").GetBoolean());
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialScan_InitializesConfigurationOnlyForSelectedPlugin(bool duplicate)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            foreach (var name in duplicate ? new[] { "first", "second" } : new[] { "first" })
            {
                WritePlugin(paths, name, "example", "");
                File.WriteAllText(Path.Combine(paths.PluginsDirectory, name, "manifest.json"),
                    """{"id":"example","name":"Example","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
                File.WriteAllText(Path.Combine(paths.PluginsDirectory, name, "settings.json"),
                    JsonSerializer.Serialize(new[] { new { name = "value", type = "text", @default = name } }));
            }
            var lifecyclePath = Path.Combine(paths.PluginDataDirectory, "example", "plugin.json");
            var configPath = Path.Combine(paths.PluginDataDirectory, "example", "config.json");
            File.Delete(lifecyclePath);
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);

            Assert.Empty(runtime.StartedIds);
            Assert.Equal(!duplicate, File.Exists(lifecyclePath));
            Assert.Equal(!duplicate, File.Exists(configPath));
            if (duplicate) Assert.Empty(manager.GetPlugins());
            else
            {
                Assert.Equal(PluginState.Disabled, Assert.Single(manager.GetPlugins()).State);
                using var config = JsonDocument.Parse(File.ReadAllText(configPath));
                Assert.Equal("first", config.RootElement.GetProperty("value").GetString());
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("incompatible")]
    [InlineData("declaration")]
    [InlineData("configuration")]
    public void Scan_DisabledSettingSaveFailureDoesNotDiscardOtherPlugins(string failure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "broken", "broken", "");
            WritePlugin(paths, "healthy", "healthy", "");
            var brokenDirectory = Path.Combine(paths.PluginsDirectory, "broken");
            var brokenData = Path.Combine(paths.PluginDataDirectory, "broken");
            Directory.CreateDirectory(Path.Combine(brokenData, "plugin.json.tmp"));
            if (failure == "incompatible")
                File.WriteAllText(Path.Combine(brokenDirectory, "manifest.json"),
                    """{"id":"broken","name":"Broken","version":"1.0.0","main":"plugin.dll","winpilot_version":"999.0.0"}""");
            else if (failure == "declaration") File.Delete(Path.Combine(brokenDirectory, "plugin.dll"));
            else
            {
                File.WriteAllText(Path.Combine(brokenDirectory, "manifest.json"),
                    """{"id":"broken","name":"Broken","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
                File.WriteAllText(Path.Combine(brokenDirectory, "settings.json"), "[]");
                Directory.CreateDirectory(Path.Combine(brokenData, "config.json.tmp"));
            }
            var runtime = new RecordingPluginRuntime();
            var logger = new CommandBotTestObserver<PluginManager>();
            using var manager = CreatePluginManager(paths, runtime, logger);

            foreach (var reload in new[] { false, true })
            {
                if (reload) manager.Scan();
                Assert.Equal(PluginState.Running, manager.GetPlugin("healthy").State);
                Assert.Equal(["healthy"], runtime.StartedIds);
                if (failure == "declaration") Assert.Single(manager.GetPlugins());
                else
                {
                    var broken = manager.GetPlugin("broken");
                    Assert.Equal(PluginState.Invalid, broken.State);
                    Assert.False(broken.Enabled);
                    Assert.False(broken.CanChangeEnabled);
                }
                using var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(brokenData, "plugin.json")));
                Assert.True(lifecycle.RootElement.GetProperty("enabled").GetBoolean());
            }
            Assert.Contains(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error
                && entry.Message.Contains("禁用设置保存失败"));
            Assert.DoesNotContain(logger.Entries, entry => entry.Message == "扫描插件目录失败");
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public void Disable_SettingSaveFailurePreservesRunningPlugin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);
            var plugin = manager.GetPlugin("example");
            var lifecyclePath = Path.Combine(plugin.DataDirectory, "plugin.json");
            var savedSettings = File.ReadAllText(lifecyclePath);
            var blockedTemporaryPath = lifecyclePath + ".tmp";
            Directory.CreateDirectory(blockedTemporaryPath);

            var exception = Record.Exception(() => manager.Disable("example"));
            Assert.True(exception is IOException or UnauthorizedAccessException);

            Assert.Same(plugin, manager.GetPlugin("example"));
            Assert.Equal(PluginState.Running, plugin.State);
            Assert.True(plugin.Enabled);
            Assert.Equal(savedSettings, File.ReadAllText(lifecyclePath));
            Assert.Empty(runtime.DisposedIds);
            Assert.Single(runtime.StartedIds);

            Directory.Delete(blockedTemporaryPath);
            manager.Disable("example");

            Assert.Equal(PluginState.Disabled, manager.GetPlugin("example").State);
            Assert.False(manager.GetPlugin("example").Enabled);
            Assert.Equal(["example"], runtime.DisposedIds);
            using var lifecycle = JsonDocument.Parse(File.ReadAllText(lifecyclePath));
            Assert.False(lifecycle.RootElement.GetProperty("enabled").GetBoolean());
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public void Enable_InitializesMissingConfigurationFromLatestDeclaration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);
            manager.Disable("example");
            File.WriteAllText(Path.Combine(paths.PluginsDirectory, "example", "manifest.json"),
                """{"id":"example","name":"Example","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
            File.WriteAllText(Path.Combine(paths.PluginsDirectory, "example", "settings.json"),
                """[{"name":"value","type":"text","default":"最新默认值"}]""");

            manager.Enable("example");

            Assert.Equal(PluginState.Running, manager.GetPlugin("example").State);
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "config.json")));
            Assert.Equal("最新默认值", config.RootElement.GetProperty("value").GetString());
        }
        finally { TryDeleteDirectory(directory); }
    }

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
            Assert.Throws<ObjectDisposedException>(() => manager.Scan());
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
            if (reload)
            {
                Directory.CreateDirectory(Path.Combine(paths.PluginDataDirectory, "example"));
                File.WriteAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json"), "{ \"enabled\": true }");
                Directory.Move(directory, directory + "-moved");
            }
            runtime.BlockStart = true;
            operation = Task.Run(() => { if (reload) manager.Scan(); else manager.Enable("example"); });
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
    public void Scan_DiscoversOnlyValidPlugins()
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
    public void WriteConfigurationAndRestart_RestartsOnlyTheEnabledTargetPlugin()
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

            manager.WriteConfiguration("plugin-a", "{}");
            manager.Restart("plugin-a");

            Assert.Equal(["plugin-a"], runtime.DisposedIds);
            Assert.Equal(["plugin-a"], runtime.StartedIds);
            Assert.Equal(PluginState.Running, manager.GetPlugin("plugin-b").State);

            manager.Disable("plugin-a");
            runtime.StartedIds.Clear();
            runtime.DisposedIds.Clear();
            manager.WriteConfiguration("plugin-a", "{}");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_DisablesIncompatiblePluginAndRejectsToggle(bool enabled)
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
            File.WriteAllText(Path.Combine(dataDirectory, "plugin.json"), JsonSerializer.Serialize(new { enabled }));
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);

            var plugin = Assert.Single(manager.GetPlugins());

            Assert.False(plugin.Enabled);
            Assert.Equal(PluginState.Invalid, plugin.State);
            Assert.False(plugin.CanChangeEnabled);
            Assert.Contains("需要 WinPilot 999.0.0 或更高版本", plugin.Errors);
            Assert.Empty(runtime.StartedIds);

            var exception = Assert.Throws<InvalidOperationException>(() => manager.Enable("future-plugin"));
            Assert.Equal("需要 WinPilot 999.0.0 或更高版本", exception.Message);
            Assert.Throws<InvalidOperationException>(() => manager.Disable("future-plugin"));
            plugin = manager.GetPlugin("future-plugin");
            Assert.False(plugin.Enabled);
            Assert.Equal(PluginState.Invalid, plugin.State);
            Assert.False(plugin.CanChangeEnabled);
            Assert.Contains("需要 WinPilot 999.0.0 或更高版本", plugin.Errors);
            using (var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "plugin.json")))) Assert.False(lifecycle.RootElement.GetProperty("enabled").GetBoolean());

            manager.Scan();
            plugin = Assert.Single(manager.GetPlugins());
            Assert.False(plugin.Enabled);
            Assert.Equal(PluginState.Invalid, plugin.State);
            Assert.Empty(runtime.StartedIds);

            File.WriteAllText(Path.Combine(directory, "manifest.json"), "{ \"id\": \"future-plugin\", \"name\": \"Future Plugin\", \"version\": \"1.0.0\", \"main\": \"plugin.dll\" }");
            manager.Scan();
            Assert.Equal(PluginState.Disabled, manager.GetPlugin("future-plugin").State);
            Assert.True(manager.GetPlugin("future-plugin").CanChangeEnabled);
            manager.Enable("future-plugin");
            Assert.Equal(PluginState.Running, manager.GetPlugin("future-plugin").State);
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Theory]
    [InlineData("missing.dll", null)]
    [InlineData("plugin.dll", "missing.json")]
    public void InitialScan_DisablesPluginWhenDeclarationValidationFails(string main, string? featuresUi)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            File.WriteAllText(Path.Combine(paths.PluginsDirectory, "example", "manifest.json"),
                JsonSerializer.Serialize(new { id = "example", name = "Example", version = "1.0.0", main, features_ui = featuresUi }));
            var runtime = new RecordingPluginRuntime();
            using var manager = CreatePluginManager(paths, runtime);

            Assert.Empty(manager.GetPlugins());
            Assert.Empty(runtime.StartedIds);
            using var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json")));
            Assert.False(lifecycle.RootElement.GetProperty("enabled").GetBoolean());
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("{\"id\":\"example\",\"name\":\"新版插件\",\"version\":\"2.0.0\",\"main\":\"plugin.dll\",\"winpilot_version\":\"999.0.0\"}", "需要 WinPilot 999.0.0 或更高版本")]
    [InlineData("{\"id\":\"example\",\"name\":\"新版插件\",\"version\":\"2.0.0\",\"main\":\"missing.dll\"}", "main 必须是插件目录内存在的 DLL 文件")]
    [InlineData("{", "manifest.json 不是有效 JSON")]
    public void Scan_PreservesRunningPluginAndRestartDisablesItOnValidationFailure(string manifest, string errorMessage)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var runtime = new RecordingPluginRuntime();
            var logger = new CommandBotTestObserver<PluginManager>();
            using var manager = CreatePluginManager(paths, runtime, logger);
            File.WriteAllText(Path.Combine(paths.PluginsDirectory, "example", "manifest.json"), manifest);

            manager.Scan();

            Assert.Equal(PluginState.Running, manager.GetPlugin("example").State);
            Assert.True(manager.GetPlugin("example").Enabled);
            Assert.Single(runtime.StartedIds);
            Assert.Empty(runtime.DisposedIds);
            using (var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json"))))
                Assert.True(lifecycle.RootElement.GetProperty("enabled").GetBoolean());

            manager.Restart("example");

            var plugin = manager.GetPlugin("example");
            Assert.False(plugin.Enabled);
            Assert.False(plugin.CanChangeEnabled);
            Assert.Equal(PluginState.Failed, plugin.State);
            Assert.Contains(plugin.Errors, error => error.Contains(errorMessage));
            Assert.Single(runtime.DisposedIds);
            Assert.Single(runtime.StartedIds);
            using (var lifecycle = JsonDocument.Parse(File.ReadAllText(Path.Combine(paths.PluginDataDirectory, "example", "plugin.json"))))
                Assert.False(lifecycle.RootElement.GetProperty("enabled").GetBoolean());
        }
        finally { TryDeleteDirectory(directory); }
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
            manager.Scan();
            Assert.Single(runtime.Instances);
            Assert.Equal([0, 1, 2, 1], runtime.Instances.Single().Counts);
            first.Dispose();
            Assert.Equal([0, 1, 2, 1, 0], runtime.Instances.Single().Counts);
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

}
