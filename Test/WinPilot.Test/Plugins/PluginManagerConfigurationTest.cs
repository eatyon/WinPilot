using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Plugins;
using WinPilot.Features.Plugins;
using static WinPilot.Test.PluginConfigurationTestData;

namespace WinPilot.Test;

public sealed class PluginManagerConfigurationTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winpilot-plugin-config", Guid.NewGuid().ToString("N"));

    private readonly PluginConfigurationStore configurationStore = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationInitializationFailureDoesNotDiscardOtherPlugins(bool blockDataDirectory)
    {
        var paths = PortableDataPaths.Create(root);
        foreach (var id in new[] { "broken", "healthy" })
        {
            var directory = Path.Combine(paths.PluginsDirectory, id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "manifest.json"),
                $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(directory, "settings.json"), Definition);
        }
        Directory.CreateDirectory(paths.PluginDataDirectory);
        var brokenData = Path.Combine(paths.PluginDataDirectory, "broken");
        if (blockDataDirectory) File.WriteAllText(brokenData, "占用目录路径");
        else Directory.CreateDirectory(Path.Combine(brokenData, "config.json.tmp"));
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, new Runtime(), pages);
        Assert.Equal(2, manager.GetPlugins().Count);
        var broken = manager.GetPlugin("broken");
        Assert.Equal(PluginState.Invalid, broken.State);
        Assert.False(broken.CanChangeEnabled);
        Assert.Contains(broken.Errors, error => error.Contains("配置初始化失败"));
        manager.Enable("healthy");
        Assert.Equal(PluginState.Running, manager.GetPlugin("healthy").State);
        manager.Disable("healthy");
        manager.Scan();
        Assert.Equal(2, manager.GetPlugins().Count);
    }

    [Fact]
    public void DiscoveryInitializesMissingConfigurationButExistingFileIsOnlyFilledOnSave()
    {
        var paths = PortableDataPaths.Create(root);
        var directory = Path.Combine(paths.PluginsDirectory, "test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            """{"id":"test","name":"Test","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
        File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
        File.WriteAllText(Path.Combine(directory, "settings.json"), Definition);
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, new Runtime(), pages);
        var data = Path.Combine(paths.PluginDataDirectory, "test");
        var path = Path.Combine(data, "config.json");
        Assert.True(File.Exists(Path.Combine(data, "plugin.json")));
        Assert.True(JsonNode.Parse(File.ReadAllText(path))!["toggle"]!.GetValue<bool>());
        const string partial = """{"text":"原值","toggle":false}""";
        File.WriteAllText(path, partial);
        manager.Scan();
        var loaded = JsonNode.Parse(manager.ReadConfiguration("test")!)!;
        Assert.Equal("原值", loaded["text"]!.GetValue<string>());
        Assert.Equal(0, loaded["count"]!.GetValue<int>());
        Assert.Equal(partial, File.ReadAllText(path));
        manager.WriteConfiguration("test", partial);
        var written = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("原值", written["text"]!.GetValue<string>());
        Assert.False(written["toggle"]!.GetValue<bool>());
        Assert.Equal(0, written["count"]!.GetValue<int>());
        Assert.False(File.Exists(path + ".tmp"));
        manager.WriteConfiguration("test", """{"count":""}""");
        Assert.Equal(0, JsonNode.Parse(File.ReadAllText(path))!["count"]!.GetValue<int>());
        var original = File.ReadAllText(path);
        Assert.Throws<PluginValidationException>(() => manager.WriteConfiguration("test", """{"toggle":"true"}"""));
        Assert.Equal(original, File.ReadAllText(path));
        foreach (var invalid in new[] { "null", "[]", "1" })
        {
            Assert.Throws<PluginValidationException>(() => manager.WriteConfiguration("test", invalid));
            Assert.Equal(original, File.ReadAllText(path));
        }
    }

    [Fact]
    public async Task DisplayAndEnableUpdatesPreserveOtherSections()
    {
        var paths = PortableDataPaths.Create(root);
        var directory = Path.Combine(paths.PluginsDirectory, "test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            """{"id":"test","name":"Test","version":"1.0.0","main":"plugin.dll"}""");
        File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, new Runtime(), pages);
        var path = Path.Combine(paths.PluginDataDirectory, "test", "plugin.json");
        configurationStore.Update(path, _ => new JsonObject { ["enabled"] = false, ["extra"] = 123 });
        await Task.WhenAll(Enumerable.Range(0, 80).Select(index => Task.Run(() =>
        {
            switch (index % 4)
            {
                case 0: manager.WriteStatusDisplaySettings("test", new(false, [], "状态")); break;
                case 1: manager.WriteFeatureDisplaySettings("test", new(false, [], "功能")); break;
                case 2: manager.WriteSettingsDisplaySettings("test", new(false)); break;
                default: manager.Enable("test"); break;
            }
        })));
        var saved = configurationStore.Read(path)!;
        Assert.True(saved["enabled"]!.GetValue<bool>());
        Assert.Equal("状态", saved["statusDisplay"]!["homeTitle"]!.GetValue<string>());
        Assert.Equal("功能", saved["featureDisplay"]!["homeTitle"]!.GetValue<string>());
        Assert.False(saved["settingsDisplay"]!["registerOnHome"]!.GetValue<bool>());
        Assert.Equal(123, saved["extra"]!.GetValue<int>());
        Assert.False(File.Exists(path + ".tmp"));
        var original = File.ReadAllText(path);
        File.WriteAllText(path, "{");
        var error = Assert.Throws<PluginOperationException>(() => manager.WriteSettingsDisplaySettings("test", new(true)));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal("{", File.ReadAllText(path));
        File.WriteAllText(path, original);
        manager.Disable("test");
    }

    [Fact]
    public async Task ConcurrentDeleteAndSavesNeverRecreateDeletedPluginData()
    {
        var paths = PortableDataPaths.Create(root);
        var directory = Path.Combine(paths.PluginsDirectory, "test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            """{"id":"test","name":"Test","version":"1.0.0","main":"plugin.dll"}""");
        File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, new Runtime(), pages);
        using var start = new ManualResetEventSlim();
        var operations = Enumerable.Range(0, 41).Select(index => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                switch (index % 4)
                {
                    case 0 when index == 0: manager.Delete("test", true); break;
                    case 0: manager.WriteConfiguration("test", "{}"); break;
                    case 1: manager.WriteStatusDisplaySettings("test", new(false)); break;
                    case 2: manager.WriteFeatureDisplaySettings("test", new(false)); break;
                    default: manager.WriteSettingsDisplaySettings("test", new(false)); break;
                }
            }
            catch (FileNotFoundException) { }
        })).ToArray();
        start.Set();
        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(manager.GetPlugins());
        Assert.False(Directory.Exists(directory));
        Assert.False(Directory.Exists(Path.Combine(paths.PluginDataDirectory, "test")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class Runtime : IPluginRuntime
    {
        public IPluginRuntimeInstance Start(PluginInfo plugin) => new Instance();
        private sealed class Instance : IPluginRuntimeInstance
        {
            public void Dispose() { }
        }
    }
}
