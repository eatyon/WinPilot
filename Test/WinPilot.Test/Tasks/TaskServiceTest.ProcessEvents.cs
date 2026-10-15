using System.Reflection;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("disable")]
    [InlineData("restart")]
    public async Task LateProcessPathLookupCannotRepopulateRetiredListenerCache(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "winpilot-listener-test.exe");
        fixture.Tasks.Create(new("process", true, new(TaskTriggerType.ProcessStarted, ProcessPaths: [path]),
            [new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
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
        foreach (var type in new[] { TaskTriggerType.ProcessStarted, TaskTriggerType.ProcessExited })
            fixture.Tasks.Create(new(type.ToString(), true, new(type, ProcessPaths: [path]),
                [new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
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
