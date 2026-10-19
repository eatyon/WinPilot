using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    [Theory]
    [InlineData("Toggle")]
    [InlineData("Enable")]
    [InlineData("Disable")]
    public async Task TaskStateHttpSaveValidatesRequiredFieldsAndReturnsSavedState(string state)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("target");
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        foreach (var missing in new[] { "taskName", "state", "" })
        {
            var request = new
            {
                name = "controller", enabled = false, trigger = new { type = "WinPilotStartup" },
                actions = new[] { new { type = "TaskState", taskName = missing == "taskName" ? null : "target", state = missing == "state" ? null : state } }
            };
            using var response = await client.PostAsJsonAsync("/api/tasks", request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (missing.Length > 0)
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("actions[0]." + missing, out _));
                Assert.False(File.Exists(Path.Combine(fixture.Paths.TasksDirectory, "controller.json")));
            }
            else
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var action = body.RootElement.GetProperty("actions")[0];
                Assert.Equal("TaskState", action.GetProperty("type").GetString());
                Assert.Equal("target", action.GetProperty("taskName").GetString());
                Assert.Equal(state, action.GetProperty("state").GetString());
                Assert.Equal(3, action.EnumerateObject().Count());
            }
        }
    }

    [Theory]
    [InlineData(TaskStateOperation.Toggle, false, true)]
    [InlineData(TaskStateOperation.Toggle, true, false)]
    [InlineData(TaskStateOperation.Enable, false, true)]
    [InlineData(TaskStateOperation.Enable, true, true)]
    [InlineData(TaskStateOperation.Disable, true, false)]
    [InlineData(TaskStateOperation.Disable, false, false)]
    public async Task TaskStateActionPersistsTargetState(TaskStateOperation state, bool initial, bool expected)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("target", initial);
        var action = new TaskAction(TaskActionType.TaskState, TaskName: "target", State: state);
        var restored = TaskActionPersistence.From(action).ToAction();
        Assert.Equal(action, restored);
        fixture.Tasks.Create(new("controller", true, new(TaskTriggerType.WinPilotStartup), [restored]));
        fixture.Tasks.RunNow("controller");
        Assert.Equal(expected, fixture.Tasks.GetAll().Single(task => task.Name == "target").Enabled);
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "target.json")));
        Assert.Equal(expected, saved.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Empty(fixture.Logger.Exceptions);
    }

    [Theory]
    [InlineData(null, TaskStateOperation.Enable, "taskName")]
    [InlineData(" ", TaskStateOperation.Disable, "taskName")]
    [InlineData("target", null, "state")]
    [InlineData("target", (TaskStateOperation)999, "state")]
    public async Task TaskStateActionRequiresTaskAndState(string? name, TaskStateOperation? state, string field)
    {
        await using var fixture = new TaskServiceFixture();
        var error = Assert.Throws<TaskValidationException>(() => fixture.Tasks.Create(new("invalid", true,
            new(TaskTriggerType.WinPilotStartup), [new(TaskActionType.TaskState, TaskName: name, State: state)])));
        Assert.Equal("actions[0]." + field, error.Field);
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisablingTaskLeavesCurrentExecutionAndRemainingActionsRunning(bool update)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken executionToken = default;
        ((IPluginTaskActionApi)scope).Register([new("wait", "Wait")], async (_, _, token) =>
        {
            executionToken = token;
            entered.TrySetResult();
            await release.Task;
            return PluginTaskActionResult.Success();
        });
        var task = fixture.Tasks.Create(new("running", true, new(TaskTriggerType.Cron, Cron: "* * * * * *"),
            [new(TaskActionType.Extension, PluginId: "test", ActionId: "wait"),
             new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.RunNow(task.Name);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (update) fixture.Tasks.Update(task.Name, task with { Enabled = false });
            else fixture.Tasks.SetEnabled(task.Name, false);
            Assert.False(executionToken.IsCancellationRequested);
            Assert.False(Assert.Single(fixture.Tasks.GetAll()).Enabled);
            fixture.Tasks.ProcessDueCronTasks(DateTimeOffset.Now, CancellationToken.None);
            Assert.Equal(0, fixture.Programs.Launches);
        }
        finally { release.TrySetResult(); }
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Programs.Launches);
    }

    [Theory]
    [InlineData(TaskStateOperation.Disable)]
    [InlineData(TaskStateOperation.Toggle)]
    public async Task TaskCanDisableItselfAndCompleteRemainingActions(TaskStateOperation state)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new("self", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.TaskState, TaskName: "self", State: state),
             new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.RunNow("self");
        Assert.False(Assert.Single(fixture.Tasks.GetAll()).Enabled);
        Assert.Equal(1, fixture.Programs.Launches);
        Assert.Empty(fixture.Logger.Exceptions);
    }

    [Fact]
    public async Task MissingTaskStateTargetFailsWithoutExecutingRemainingActions()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("target");
        fixture.Tasks.Create(new("controller", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.TaskState, TaskName: "target", State: TaskStateOperation.Disable),
             new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.Delete("target");
        fixture.Tasks.RunNow("controller");
        Assert.IsType<FileNotFoundException>(Assert.Single(fixture.Logger.Exceptions));
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Fact]
    public async Task TaskStateSaveFailurePreservesTargetStateAndStopsRemainingActions()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("target");
        fixture.Tasks.Create(new("controller", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.TaskState, TaskName: "target", State: TaskStateOperation.Disable),
             new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var file = Path.Combine(fixture.Paths.TasksDirectory, "target.json");
        var original = File.ReadAllText(file);
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            fixture.Tasks.RunNow("controller");
        Assert.True(fixture.Tasks.GetAll().Single(task => task.Name == "target").Enabled);
        Assert.Equal(original, File.ReadAllText(file));
        Assert.True(Assert.Single(fixture.Logger.Exceptions) is IOException or UnauthorizedAccessException);
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Fact]
    public async Task ConcurrentTaskStateTogglesDoNotLoseUpdates()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("target", false);
        for (var index = 0; index < 20; index++)
            fixture.Tasks.Create(new("toggle-" + index, true, new(TaskTriggerType.WinPilotStartup),
                [new(TaskActionType.TaskState, TaskName: "target", State: TaskStateOperation.Toggle)]));
        Parallel.For(0, 20, index => fixture.Tasks.RunNow("toggle-" + index));
        Assert.False(fixture.Tasks.GetAll().Single(task => task.Name == "target").Enabled);
        Assert.Empty(fixture.Logger.Exceptions);
    }
}
