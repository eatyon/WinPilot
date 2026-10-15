using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;

namespace WinPilot.Test;

public sealed class TaskTriggerPersistenceTest
{
    [Fact]
    public void ExtensionTrigger_PersistsOnlyStableIdentity()
    {
        var stored = TaskTriggerPersistence.From(new TaskTrigger(TaskTriggerType.Extension, PluginId: "test-plugin", TriggerId: "test-trigger"));

        Assert.Equal("Extension", stored.Type);
        Assert.Equal("test-plugin", stored.PluginId);
        Assert.Equal("test-trigger", stored.TriggerId);
        Assert.Null(stored.Cron);
        Assert.Null(stored.ProcessPaths);
    }
}
