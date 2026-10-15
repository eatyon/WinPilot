using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using System.Text.Json;
using WinPilot.Features.Plugins;
using static WinPilot.Test.PluginTestSupport;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{
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
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
            Assert.False(manager.HasReadme("readme-plugin"));
            Assert.Throws<FileNotFoundException>(() => manager.ReadReadme("readme-plugin"));

            var readmePath = Path.Combine(directory, "README.md");
            File.WriteAllText(readmePath, "# 插件说明\n\n测试内容");
            Assert.True(manager.HasReadme("readme-plugin"));
            Assert.Equal("# 插件说明\n\n测试内容", manager.ReadReadme("readme-plugin"));

            File.WriteAllBytes(readmePath, new byte[512 * 1024 + 1]);
            Assert.Throws<PluginValidationException>(() => manager.ReadReadme("readme-plugin"));
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

            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);

            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);

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
}
