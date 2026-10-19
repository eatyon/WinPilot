using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    [Theory]
    [InlineData(".bat", "程序事件仅支持 EXE 文件")]
    [InlineData(".cmd", "程序事件仅支持 EXE 文件")]
    [InlineData(".vbs", "程序事件仅支持 EXE 文件")]
    [InlineData(".py", "程序事件仅支持 EXE 文件")]
    [InlineData(".txt", "程序事件仅支持 EXE 文件")]
    [InlineData(".exe", "找不到指定的程序文件")]
    public async Task ProcessEventSaveRejectsNonExeAndMissingFilesWithoutChangingTheTask(string extension, string error)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Original", enabled: false);
        var originalFile = Path.Combine(fixture.Paths.TasksDirectory, "Original.json");
        var originalContent = File.ReadAllText(originalFile);
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "unavailable" + extension);
        if (extension != ".exe") File.WriteAllText(path, "test script");
        var task = new WinPilotTask("Changed", false,
            new(TaskTriggerType.Process, ProcessPaths: [Environment.ProcessPath!, path], Event: TaskProcessEventType.Started),
            [new(TaskActionType.Delay, DelaySeconds: 1)]);
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? "/api/tasks" : "/api/tasks/Original")
            { Content = JsonContent.Create(task) };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(error,
                problem.RootElement.GetProperty("errors").GetProperty("trigger.processPaths")[0].GetString());
            Assert.Equal(originalContent, File.ReadAllText(originalFile));
            Assert.Equal("Original", Assert.Single(fixture.Tasks.GetAll()).Name);
            Assert.Single(Directory.GetFiles(fixture.Paths.TasksDirectory));
            Assert.Empty(fixture.Programs.Requests);
        }
    }

    [Theory]
    [InlineData(TaskProcessEventType.Started)]
    [InlineData(TaskProcessEventType.Exited)]
    public async Task ProcessEventSaveAcceptsExistingExeWithoutLaunching(TaskProcessEventType processEvent)
    {
        await using var fixture = new TaskServiceFixture();
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "test.EXE");
        File.WriteAllText(path, "test program");
        var task = new WinPilotTask("Process", false,
            new(TaskTriggerType.Process, ProcessPaths: [path, Environment.ProcessPath!], Event: processEvent),
            [new(TaskActionType.Delay, DelaySeconds: 1)]);
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        using var created = await client.PostAsJsonAsync("/api/tasks", task);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var updated = await client.PutAsJsonAsync("/api/tasks/Process", task with { Name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(task.Trigger.ProcessPaths, Assert.Single(fixture.Tasks.GetAll()).Trigger.ProcessPaths);
        Assert.Empty(fixture.Programs.Requests);
    }

    [Fact]
    public async Task LoadingKeepsProcessEventTaskWhenExecutableIsUnavailable()
    {
        string content = "";
        await using var fixture = new TaskServiceFixture(initialize: paths =>
        {
            Directory.CreateDirectory(paths.TasksDirectory);
            var task = new WinPilotTask("Unavailable", false,
                new(TaskTriggerType.Process, ProcessPaths: [Path.Combine(paths.ApplicationDirectory, "missing.exe")], Event: TaskProcessEventType.Started),
                [new(TaskActionType.Delay, DelaySeconds: 1)]);
            content = JsonSerializer.Serialize(TaskPersistenceFile.From(task));
            File.WriteAllText(Path.Combine(paths.TasksDirectory, "Unavailable.json"), content);
        });
        var loaded = Assert.Single(fixture.Tasks.GetAll());
        Assert.False(File.Exists(Assert.Single(loaded.Trigger.ProcessPaths!)));
        Assert.Empty(fixture.Logger.Exceptions);
        var error = Assert.Throws<TaskValidationException>(() => fixture.Tasks.Update(loaded.Name, loaded));
        Assert.Equal("trigger.processPaths", error.Field);
        Assert.Equal("找不到指定的程序文件", error.Message);
        Assert.Equal(content, File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Unavailable.json")));
        fixture.Tasks.Delete(loaded.Name);
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("disable")]
    [InlineData("restart")]
    public async Task LateProcessPathLookupCannotRepopulateRetiredListenerCache(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "winpilot-listener-test.exe");
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        File.WriteAllText(path, "test program");
        fixture.Tasks.Create(new("process", true, new(TaskTriggerType.Process, ProcessPaths: [path], Event: TaskProcessEventType.Started),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        SetProcessListenerState(fixture.Tasks, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var callbacks = 0;
        var pending = Task.Run(() => fixture.Tasks.ProcessEvent(new(WindowsProcessEventType.Started, 123456, "winpilot-listener-test"), id =>
        {
            Assert.Equal(123456, id);
            Interlocked.Increment(ref callbacks);
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return path;
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (operation)
            {
                case "stop": await fixture.Tasks.StopAsync(CancellationToken.None); break;
                case "dispose": fixture.Tasks.Dispose(); break;
                case "disable": fixture.Tasks.SetEnabled("process", false); break;
                case "restart":
                    fixture.Tasks.SetEnabled("process", false);
                    SetProcessListenerState(fixture.Tasks, false);
                    fixture.Tasks.SetEnabled("process", true);
                    SetProcessListenerState(fixture.Tasks, true);
                    break;
            }
            Assert.Empty(ProcessPathSnapshot(fixture.Tasks));
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, callbacks);
        Assert.Empty(fixture.Logger.Exceptions);
        Assert.Empty(ProcessPathSnapshot(fixture.Tasks));
        Assert.Equal(0, fixture.Programs.Launches);
        var queries = 0;
        fixture.Tasks.ProcessEvent(new(WindowsProcessEventType.Started, 123457, "winpilot-listener-test"), _ => { queries++; return path; });
        if (operation == "restart")
        {
            Assert.Equal(1, queries);
            Assert.Equal(path, Assert.Single(ProcessPathSnapshot(fixture.Tasks)).Value);
            Assert.Equal(1, fixture.Programs.Launches);
        }
        else
        {
            Assert.Equal(0, queries);
            Assert.Empty(ProcessPathSnapshot(fixture.Tasks));
            Assert.Equal(0, fixture.Programs.Launches);
        }
    }

    [Fact]
    public async Task ProcessStartCachesPathAndExitConsumesItWithoutAnotherLookup()
    {
        await using var fixture = new TaskServiceFixture();
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "winpilot-listener-test.exe");
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        File.WriteAllText(path, "test program");
        foreach (var processEvent in new[] { TaskProcessEventType.Started, TaskProcessEventType.Exited })
            fixture.Tasks.Create(new(processEvent.ToString(), true, new(TaskTriggerType.Process, ProcessPaths: [path], Event: processEvent),
                [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        SetProcessListenerState(fixture.Tasks, true);
        var queries = 0;
        string Resolve(int id) { Assert.Equal(123456, id); queries++; return path; }
        fixture.Tasks.ProcessEvent(new(WindowsProcessEventType.Started, 123456, "winpilot-listener-test"), Resolve);
        Assert.Equal(path, Assert.Single(ProcessPathSnapshot(fixture.Tasks)).Value);
        Assert.Equal(1, queries);
        Assert.Equal(1, fixture.Programs.Launches);
        fixture.Tasks.ProcessEvent(new(WindowsProcessEventType.Exited, 123456, "winpilot-listener-test"), Resolve);
        Assert.Empty(ProcessPathSnapshot(fixture.Tasks));
        Assert.Equal(1, queries);
        Assert.Equal(2, fixture.Programs.Launches);
        fixture.Tasks.ProcessEvent(new(WindowsProcessEventType.Exited, 123456, "winpilot-listener-test"), Resolve);
        Assert.Equal(2, fixture.Programs.Launches);
        Assert.Empty(fixture.Logger.Exceptions);
    }

    [Fact]
    public async Task ExitOnlyTaskCachesStartedPathWithoutRunningUntilExit()
    {
        await using var fixture = new TaskServiceFixture();
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "winpilot-listener-test.exe");
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        File.WriteAllText(path, "test program");
        fixture.Tasks.Create(new("exit", true, new(TaskTriggerType.Process, ProcessPaths: [path], Event: TaskProcessEventType.Exited),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        SetProcessListenerState(fixture.Tasks, true);
        var queries = 0;
        string Resolve(int _) { queries++; return path; }
        fixture.Tasks.ProcessEvent(new(WindowsProcessEventType.Started, 123456, "winpilot-listener-test"), Resolve);
        Assert.Equal(0, fixture.Programs.Launches);
        Assert.Equal(path, Assert.Single(ProcessPathSnapshot(fixture.Tasks)).Value);
        fixture.Tasks.ProcessEvent(new(WindowsProcessEventType.Exited, 123456, "winpilot-listener-test"), Resolve);
        Assert.Equal(1, queries);
        Assert.Equal(1, fixture.Programs.Launches);
        Assert.Empty(ProcessPathSnapshot(fixture.Tasks));
    }

    private static void SetProcessListenerState(TaskService service, bool started)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var gate = typeof(TaskService).GetField("gate", flags)!.GetValue(service)!;
        lock (gate)
        {
            typeof(TaskService).GetField("taskServiceStarted", flags)!.SetValue(service, started);
            typeof(TaskService).GetField("processListenerStarted", flags)!.SetValue(service, started);
        }
    }

    private static IReadOnlyDictionary<int, string> ProcessPathSnapshot(TaskService service)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var gate = typeof(TaskService).GetField("gate", flags)!.GetValue(service)!;
        lock (gate) return new Dictionary<int, string>((Dictionary<int, string>)typeof(TaskService).GetField("processPaths", flags)!.GetValue(service)!);
    }
}
