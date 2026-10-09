using WinPilot.Core;
using System.Text.Json;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class PluginTaskExtensionRegistryTest
{
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
}
