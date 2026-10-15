using WinPilot.Features.Plugins;
using WinPilot.Features.Tasks;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class TaskVariableCatalogTest
{
    [Fact]
    public void TaskVariableCatalogCombinesHostAndRegisteredPluginVariables()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test Plugin");
        ((IPluginNotificationVariableApi)scope).Register([
            new("$test.summary", "Summary"),
            new("&test.image", "Image", Type: PluginVariableType.Image)
        ]);

        var catalog = new TaskVariableCatalog(registry);
        var variables = catalog.GetAll();

        Assert.Contains(variables, variable => variable.Name == "$time" && variable.PluginId == "winpilot");
        Assert.Contains(variables, variable => variable.Name == "&screenshot" && variable.Type == PluginVariableType.Image);
        Assert.Contains(variables, variable => variable.Name == "&image=" && variable.PluginId == "winpilot");
        Assert.Contains(variables, variable => variable.Name == "$test.summary" && variable.PluginId == "test-plugin");
        Assert.True(catalog.IsRegisteredImage("&screenshot"));
        Assert.True(catalog.IsRegisteredImage("&test.image"));
        Assert.False(catalog.IsRegisteredImage("&image="));
        Assert.False(catalog.IsRegisteredImage("&unknown.image"));
    }
}
