using WinPilot.Core;
using System.Text.Json;
using WinPilot.Features.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{

    [Fact]
    public async Task PageSession_ForwardsMessagesAndRejectsAfterClose()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var id = sessions.Open("test-plugin", "test-page", session => session.RegisterMessageHandler((message, _) => Task.FromResult("reply:" + message)));

        Assert.Equal("reply:request", await sessions.SendAsync(id, "request", CancellationToken.None));

        sessions.Close(id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sessions.SendAsync(id, "request", CancellationToken.None));
    }

    [Fact]
    public async Task PageSession_CloseAllCancelsActiveRequestsAndRemovesSessions()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var id = sessions.Open("test-plugin", "test-page", session =>
            session.RegisterMessageHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "unreachable";
            }));
        var request = sessions.SendAsync(id, "request", CancellationToken.None);

        sessions.CloseAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sessions.SendAsync(id, "request", CancellationToken.None));
    }

    [Fact]
    public async Task ScanPreservesExistingPageSessionsAndClosesRemovedPluginSessions()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var directory = Path.Combine(paths.PluginsDirectory, "page-plugin");
            Directory.CreateDirectory(Path.Combine(directory, "web"));
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), """
                { "id": "page-plugin", "name": "Page Plugin", "version": "1.0.0", "main": "plugin.dll", "features_ui": "features.json" }
                """);
            File.WriteAllText(Path.Combine(directory, "features.json"), """
                { "features": [{ "id": "page", "name": "Page", "mode": "page", "entry": "page.js" }] }
                """);
            var scriptPath = Path.Combine(directory, "web", "page.js");
            File.WriteAllText(scriptPath, "const value = 1;");
            Directory.CreateDirectory(Path.Combine(paths.PluginDataDirectory, "page-plugin"));
            File.WriteAllText(Path.Combine(paths.PluginDataDirectory, "page-plugin", "plugin.json"), "{ \"enabled\": true }");
            var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using (sessions)
            {
                using var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, new PagePluginRuntime(), sessions);
                var resources = new PluginWebResourceService(manager);
                var webVersion = resources.GetVersion("page-plugin");
                Assert.Equal(webVersion, resources.GetVersion("page-plugin"));
                Assert.NotNull(resources.Resolve("page-plugin", "page.js"));
                File.WriteAllText(scriptPath, "const value = 10;");
                Assert.Equal(webVersion, resources.GetVersion("page-plugin", refresh: false));
                Assert.NotEqual(webVersion, resources.GetVersion("page-plugin"));
                var pageSession = manager.OpenPageFeatureSession("page-plugin", "page");
                var id = pageSession.Id;
                Assert.Equal("Page Plugin", pageSession.PluginName);
                Assert.Equal("page", pageSession.Feature.GetProperty("id").GetString());
                Assert.Equal("page.js", pageSession.Feature.GetProperty("entry").GetString());
                Assert.Equal("reply", await manager.SendPageMessageAsync(id, "request", CancellationToken.None));

                manager.Scan();

                Assert.Equal("reply", await manager.SendPageMessageAsync(id, "request", CancellationToken.None));
                Directory.Delete(directory, true);
                manager.Scan();
                await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.SendPageMessageAsync(id, "request", CancellationToken.None));
            }
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Fact]
    public void FeaturesDefinition_ReadsDeclarationsWithoutStartingRuntime()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var directory = Path.Combine(paths.PluginsDirectory, "features-example");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), "{ \"id\": \"features-example\", \"name\": \"Features Example\", \"version\": \"1.0.0\", \"main\": \"plugin.dll\", \"features_ui\": \"features.json\" }");
            File.WriteAllText(Path.Combine(directory, "features.json"), "{ \"features\": [{ \"id\": \"refresh\", \"name\": \"刷新\", \"mode\": \"action\", \"confirm\": false }, { \"id\": \"clear-cache\", \"name\": \"清理缓存\", \"mode\": \"action\" }, { \"id\": \"advanced-tools\", \"name\": \"高级功能\", \"mode\": \"action\" }] }");

            var runtime = new RecordingPluginRuntime();
            var manager = CreatePluginManager(paths, runtime);

            using var features = JsonDocument.Parse(manager.ReadFeaturesDefinition("features-example"));
            Assert.Equal(["action", "action", "action"], features.RootElement.GetProperty("features").EnumerateArray().Select(feature => feature.GetProperty("mode").GetString()));
            Assert.Equal(["refresh", "clear-cache", "advanced-tools"], manager.GetFeatureDisplaySettings("features-example").FeatureIds);
            var display = manager.WriteFeatureDisplaySettings("features-example", new PluginManager.PluginFeatureDisplaySettings(true, ["refresh", "missing"], "首页功能"));
            Assert.Equal("首页功能", display.HomeTitle);
            Assert.Equal(["refresh"], display.FeatureIds);
            var reloadedDisplay = manager.GetFeatureDisplaySettings("features-example");
            Assert.True(reloadedDisplay.RegisterOnHome);
            Assert.Equal("首页功能", reloadedDisplay.HomeTitle);
            Assert.Equal(["refresh"], reloadedDisplay.FeatureIds);
            Assert.Empty(runtime.StartedIds);
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Fact]
    public void Scan_AllowsMultiplePageFeaturesWithoutManifestWebDeclaration()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var directory = Path.Combine(paths.PluginsDirectory, "page-features");
            Directory.CreateDirectory(Path.Combine(directory, "web"));
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), """
                { "id": "page-features", "name": "Page Features", "version": "1.0.0", "main": "plugin.dll", "features_ui": "features.json" }
                """);
            File.WriteAllText(Path.Combine(directory, "features.json"), """
                { "features": [
                    { "id": "first-page", "name": "第一个页面", "mode": "page", "entry": "first.js" },
                    { "id": "second-page", "name": "第二个页面", "mode": "page", "entry": "tools/second.js" }
                ] }
                """);
            File.WriteAllText(Path.Combine(directory, "web", "first.js"), "");
            Directory.CreateDirectory(Path.Combine(directory, "web", "tools"));
            File.WriteAllText(Path.Combine(directory, "web", "tools", "second.js"), "");

            var manager = CreatePluginManager(paths, new RecordingPluginRuntime());
            var plugin = Assert.Single(manager.GetPlugins());

            Assert.Equal("page-features", plugin.Id);
            Assert.Empty(plugin.Errors);
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }


}
