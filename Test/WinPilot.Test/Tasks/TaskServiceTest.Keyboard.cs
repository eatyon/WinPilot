using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    private static WinPilotTask KeyboardTask(string name = "keyboard") => new(name, true,
        new(TaskTriggerType.Keyboard, Keys: ["Ctrl", "A", "B"]), [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]);

    [Fact]
    public async Task KeyboardContractPrioritizesModifiersAndOmitsUnrelatedTriggerFields()
    {
        await using var fixture = new TaskServiceFixture();
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/tasks", KeyboardTask() with { Trigger = new(TaskTriggerType.Keyboard, Keys: ["A", "Ctrl", "B"]) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var trigger = json.RootElement.GetProperty("trigger");
        Assert.Equal(new[] { "type", "keys" }, trigger.EnumerateObject().Select(property => property.Name));
        Assert.Equal(new[] { "Ctrl", "A", "B" }, trigger.GetProperty("keys").EnumerateArray().Select(value => value.GetString()));
        var content = File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "keyboard.json"));
        using var stored = JsonDocument.Parse(content);
        Assert.Equal(new[] { "Ctrl", "A", "B" }, stored.RootElement.GetProperty("trigger").GetProperty("keys").EnumerateArray().Select(value => value.GetString()));
        using var invalid = await client.PostAsJsonAsync("/api/tasks", KeyboardTask("invalid") with { Trigger = new(TaskTriggerType.Keyboard, Keys: ["NotAKey"]) });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        Assert.Equal("包含不支持的按键", problem.RootElement.GetProperty("errors").GetProperty("trigger.keys")[0].GetString());
        Assert.Single(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData("[]", "请选择至少一个按键")]
    [InlineData("[\"A\",\"B\",\"C\",\"D\"]", "最多选择 3 个按键")]
    [InlineData("[\"A\",\"a\"]", "按键不能重复")]
    [InlineData("[\"999\"]", "包含不支持的按键")]
    [InlineData("[\"NoSuchKey\"]", "包含不支持的按键")]
    public async Task InvalidKeyboardTriggerHasAFieldErrorAndDoesNotSave(string keys, string expected)
    {
        await using var fixture = new TaskServiceFixture();
        var task = KeyboardTask() with { Trigger = new(TaskTriggerType.Keyboard, Keys: JsonSerializer.Deserialize<string[]>(keys)) };
        var error = Assert.Throws<TaskValidationException>(() => fixture.Tasks.Create(task));
        Assert.Equal("trigger.keys", error.Field);
        Assert.Equal(expected, error.Message);
        Assert.Empty(fixture.Tasks.GetAll());
        Assert.False(Directory.Exists(fixture.Paths.TasksDirectory));
    }

    [Theory]
    [InlineData("A,Ctrl,B", "Ctrl,A,B")]
    [InlineData("B,A,Shift", "Shift,B,A")]
    [InlineData("A,Win,Alt", "Win,Alt,A")]
    [InlineData("B,A,C", "B,A,C")]
    [InlineData("Shift,Ctrl,Win", "Win,Ctrl,Shift")]
    [InlineData("Shift,Alt,Ctrl", "Ctrl,Alt,Shift")]
    public async Task KeyboardNormalizationUsesFixedModifierOrderAndKeepsNormalKeyOrder(string input, string expected)
    {
        await using var fixture = new TaskServiceFixture();
        var keys = input.Split(',');
        var saved = fixture.Tasks.Create(KeyboardTask() with { Trigger = new(TaskTriggerType.Keyboard, Keys: keys) });
        Assert.Equal(expected.Split(','), saved.Trigger.Keys);
        Assert.Equal(input.Split(','), keys);
    }

    [Fact]
    public async Task LoadedKeyboardKeysUseModifierFirstOrderWithoutRewritingTheFile()
    {
        const string content = """
            {"name":"keyboard","enabled":false,"trigger":{"type":"Keyboard","keys":["B","Ctrl","A"]},"actions":[{"type":"StartProgram","programPath":"app.exe"}]}
            """;
        string file = "";
        await using var fixture = new TaskServiceFixture(initialize: paths =>
        {
            Directory.CreateDirectory(paths.TasksDirectory);
            file = Path.Combine(paths.TasksDirectory, "keyboard.json");
            File.WriteAllText(file, content);
        });
        Assert.Equal(new[] { "Ctrl", "B", "A" }, Assert.Single(fixture.Tasks.GetAll()).Trigger.Keys);
        Assert.Equal(content, File.ReadAllText(file));
    }

    [Fact]
    public async Task KeyboardActionsAndTriggersShareTheFixedModifierOrder()
    {
        await using var fixture = new TaskServiceFixture();
        var saved = fixture.Tasks.Create(KeyboardTask() with
        {
            Trigger = new(TaskTriggerType.Keyboard, Keys: ["Shift", "Ctrl", "Win"]),
            Actions = [new(TaskActionType.Keyboard, Keys: ["Shift", "Alt", "Ctrl"])]
        });
        Assert.Equal(new[] { "Win", "Ctrl", "Shift" }, saved.Trigger.Keys);
        Assert.Equal(new[] { "Ctrl", "Alt", "Shift" }, Assert.Single(saved.Actions).Keys);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("stop")]
    public async Task RetiredKeyboardSnapshotCannotExecute(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        var task = fixture.Tasks.Create(KeyboardTask());
        await fixture.Tasks.StartAsync(CancellationToken.None);
        await fixture.Logger.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(async () => { captured.SetResult(); await release.Task; await fixture.Tasks.TriggerKeyboardTask(task); });
        try
        {
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (operation)
            {
                case "edit": fixture.Tasks.Update(task.Name, task); break;
                case "disable": fixture.Tasks.SetEnabled(task.Name, false); break;
                case "delete": fixture.Tasks.Delete(task.Name); break;
                case "rename": fixture.Tasks.Update(task.Name, task with { Name = "renamed" }); break;
                case "stop": await fixture.Tasks.StopAsync(CancellationToken.None); break;
            }
        }
        finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Fact]
    public async Task KeyboardDispatchDoesNotBlockInputAndIgnoresReentryUntilExecutionFinishes()
    {
        await using var fixture = new TaskServiceFixture();
        var task = fixture.Tasks.Create(KeyboardTask());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Programs.BeforeLaunch = _ => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        await fixture.Tasks.StartAsync(CancellationToken.None);
        await fixture.Logger.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = fixture.Tasks.TriggerKeyboardTask(task);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Tasks.TriggerKeyboardTask(task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
        }
        finally { release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, fixture.Programs.Launches);
        await fixture.Tasks.TriggerKeyboardTask(task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Programs.Launches);
        Assert.Contains("任务「keyboard」开始执行，触发：按键监听「Ctrl + A + B」", fixture.Logger.Messages);
    }
}
