using System.Text.Json;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;

namespace WinPilot.Test;

public sealed class TaskTriggerPersistenceTest
{
    [Theory]
    [InlineData(TaskProcessEventType.Started)]
    [InlineData(TaskProcessEventType.Exited)]
    public void ProcessTriggerPersistsEventAndPathsWithoutUnrelatedFields(TaskProcessEventType processEvent)
    {
        var trigger = new TaskTrigger(TaskTriggerType.Process, Cron: "unused", ProcessPaths: ["C:\\app.exe"],
            PluginId: "unused", TriggerId: "unused", Event: processEvent);
        var stored = TaskTriggerPersistence.From(trigger);
        Assert.Equal("Process", stored.Type);
        Assert.Equal(processEvent.ToString(), stored.Event);
        Assert.Equal(trigger.ProcessPaths, stored.ProcessPaths);
        Assert.Null(stored.Cron);
        Assert.Null(stored.PluginId);
        Assert.Null(stored.TriggerId);
        Assert.Null(stored.Parameters);
        var restored = stored.ToTrigger();
        Assert.Equal(TaskTriggerType.Process, restored.Type);
        Assert.Equal(processEvent, restored.Event);
        Assert.Equal(trigger.ProcessPaths, restored.ProcessPaths);
    }

    [Theory]
    [InlineData(TaskTriggerType.Cron)]
    [InlineData(TaskTriggerType.WinPilotStartup)]
    [InlineData(TaskTriggerType.Extension)]
    public void OtherTriggersDoNotPersistAStaleProcessEvent(TaskTriggerType type)
    {
        var stored = TaskTriggerPersistence.From(new(type, Event: TaskProcessEventType.Exited));
        Assert.Null(stored.Event);
        Assert.Null(stored.ToTrigger().Event);
    }

    [Theory]
    [InlineData(TaskProcessEventType.Started)]
    [InlineData(TaskProcessEventType.Exited)]
    public async Task SavedProcessFilesRestoreEventAndPathsWithoutRewritingTheFile(TaskProcessEventType processEvent)
    {
        var content = JsonSerializer.Serialize(new
        {
            name = "process", enabled = false,
            trigger = new { type = "Process", @event = processEvent.ToString(), processPaths = new[] { "C:\\app.exe" } },
            actions = new[] { new { type = "StartProgram", programPath = Environment.ProcessPath! } }
        });
        string file = "";
        await using var fixture = new TaskServiceFixture(initialize: paths =>
        {
            Directory.CreateDirectory(paths.TasksDirectory);
            file = Path.Combine(paths.TasksDirectory, "process.json");
            File.WriteAllText(file, content);
        });
        var task = Assert.Single(fixture.Tasks.GetAll());
        Assert.Equal(TaskTriggerType.Process, task.Trigger.Type);
        Assert.Equal(processEvent, task.Trigger.Event);
        Assert.Equal("C:\\app.exe", Assert.Single(task.Trigger.ProcessPaths!));
        Assert.Empty(fixture.Logger.Exceptions);
        Assert.Equal(content, File.ReadAllText(file));
    }

    [Theory]
    [InlineData("ProcessStarted")]
    [InlineData("ProcessExited")]
    [InlineData("Process")]
    [InlineData("Process", "Unknown")]
    public async Task InvalidOrLegacyProcessFilesAreSkippedWithoutChangingTheOriginal(string type, string? processEvent = null)
    {
        var content = JsonSerializer.Serialize(new
        {
            name = "invalid", enabled = false,
            trigger = new { type, @event = processEvent, processPaths = new[] { "C:\\app.exe" } },
            actions = new[] { new { type = "StartProgram", programPath = Environment.ProcessPath! } }
        });
        string file = "";
        await using var fixture = new TaskServiceFixture(initialize: paths =>
        {
            Directory.CreateDirectory(paths.TasksDirectory);
            file = Path.Combine(paths.TasksDirectory, "invalid.json");
            File.WriteAllText(file, content);
        });
        Assert.Empty(fixture.Tasks.GetAll());
        Assert.Single(fixture.Logger.Exceptions);
        Assert.Equal(content, File.ReadAllText(file));
    }

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
