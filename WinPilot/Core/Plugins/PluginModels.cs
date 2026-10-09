using System.Text.Json.Serialization;

namespace WinPilot.Core.Plugins;

public enum PluginState
{
    Discovered,
    Disabled,
    Loaded,
    Running,
    Stopped,
    Failed,
    Invalid
}

public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<PluginAuthor>? Authors,
    string? Description,
    [property: JsonPropertyName("winpilot_version")] string? WinPilotVersion,
    [property: JsonPropertyName("status_ui")] bool StatusUi,
    [property: JsonPropertyName("features_ui")] string? FeaturesUi,
    [property: JsonPropertyName("settings_ui")] string? SettingsUi,
    string Main);

public sealed record PluginAuthor(string? Name, string? Links);

public sealed record PluginInfo(
    string Id,
    string DirectoryName,
    string Directory,
    string DataDirectory,
    PluginManifest? Manifest,
    PluginState State,
    bool Enabled,
    IReadOnlyList<string> Errors,
    bool CanChangeEnabled)
{
    public bool HasError => Errors.Count > 0;
}
