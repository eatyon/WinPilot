using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class TaskExecutionLifetimeTest
{
    [Theory]
    [InlineData("disable")]
    [InlineData("rename")]
    [InlineData("rename-during-cancel")]
    [InlineData("delete")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task CancellationReleasesTaskLockAndKeepsSourceUntilCallbackAndExecutionFinish(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var entered = Signal();
        var releaseAction = Signal();
        var releaseNew = Signal();
        using var releaseCallback = new ManualResetEventSlim();
        CancellationTokenRegistration registration = default;
        var calls = 0;
        ((IPluginTaskActionApi)scope).Register([new("run", "Run")], async (_, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                registration = token.Register(() =>
                {
                    entered.TrySetResult();
                    Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(10)));
                });
                await releaseAction.Task;
            }
            else await releaseNew.Task;
            return PluginTaskActionResult.Success();
        });
        fixture.Tasks.Create(new("cancel", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Extension, PluginId: "test", ActionId: "run"), new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
        fixture.Tasks.RunNow("cancel");
        var record = Running(fixture.Tasks)["cancel"]!;
        var source = Source(record);
        var cancelling = Task.Run(async () =>
        {
            switch (operation)
            {
                case "disable": fixture.Tasks.SetEnabled("cancel", false); break;
                case "rename-during-cancel": fixture.Tasks.SetEnabled("cancel", false); break;
                case "rename": fixture.Tasks.Update("cancel", fixture.Tasks.GetAll().Single() with { Name = "renamed" }); break;
                case "delete": fixture.Tasks.Delete("cancel"); break;
                case "stop": await fixture.Tasks.StopAsync(CancellationToken.None); break;
                case "dispose": fixture.Tasks.Dispose(); break;
            }
        });
        Task? probing = null;
        object? replacement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            probing = Task.Run(() =>
            {
                _ = fixture.Tasks.GetAll();
                if (operation is "rename" or "rename-during-cancel")
                {
                    if (operation == "rename-during-cancel")
                        fixture.Tasks.Update("cancel", fixture.Tasks.GetAll().Single() with { Name = "renamed" });
                    fixture.Tasks.RunNow("renamed");
                    replacement = Running(fixture.Tasks)["renamed"]!;
                    fixture.Tasks.RunNow("renamed");
                    Assert.Equal(2, calls);
                }
            });
            await probing.WaitAsync(TimeSpan.FromSeconds(5));
            releaseAction.TrySetResult();
            await Execution(record).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(source.Token.IsCancellationRequested);
            Assert.Equal(0, fixture.Programs.Launches);
            if (replacement is not null)
            {
                Assert.Same(replacement, Running(fixture.Tasks)["renamed"]);
                Assert.False(Source(replacement).IsCancellationRequested);
            }
        }
        finally
        {
            releaseAction.TrySetResult();
            releaseCallback.Set();
            try { await Task.WhenAll(cancelling, probing ?? Task.CompletedTask, Execution(record)).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                releaseNew.TrySetResult();
                if (replacement is not null) await Execution(replacement).WaitAsync(TimeSpan.FromSeconds(5));
                registration.Dispose();
            }
        }
        Assert.Throws<ObjectDisposedException>(() => source.Token);
        if (replacement is not null) await Execution(replacement).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BatchCancellationMarksAllExecutionsBeforeInvokingFirstCallback()
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var entered = Signal();
        var releaseFirst = Signal();
        var releaseSecond = Signal();
        using var releaseCallback = new ManualResetEventSlim();
        CancellationTokenRegistration registration = default;
        ((IPluginTaskActionApi)scope).Register([new("run", "Run")], async (_, context, token) =>
        {
            if (context.TaskName == "first")
            {
                registration = token.Register(() =>
                {
                    entered.TrySetResult();
                    Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(10)));
                });
                await releaseFirst.Task;
            }
            else await releaseSecond.Task;
            return PluginTaskActionResult.Success();
        });
        foreach (var name in new[] { "first", "second" })
        {
            fixture.Tasks.Create(new(name, false, new(TaskTriggerType.WinPilotStartup),
                [new(TaskActionType.Extension, PluginId: "test", ActionId: "run"), new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
            fixture.Tasks.RunNow(name);
        }
        var second = Running(fixture.Tasks)["second"]!;
        var stopping = Task.Run(() => fixture.Tasks.StopAsync(CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseSecond.TrySetResult();
            await Execution(second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Source(second).Token.IsCancellationRequested);
            Assert.Equal(0, fixture.Programs.Launches);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            releaseCallback.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            registration.Dispose();
        }
        Assert.Throws<ObjectDisposedException>(() => Source(second).Token);
    }

    [Fact]
    public async Task SynchronousRunsLeaveNoTrackingAndCanRepeat()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("sync");
        fixture.Tasks.RunNow("sync");
        fixture.Tasks.RunNow("sync");
        Assert.Equal(2, fixture.Programs.Launches);
        Assert.Empty(Running(fixture.Tasks).Keys.Cast<object>());
        await fixture.Tasks.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CooperativeCancellationWaitsForActionFinallyBeforeReleasingSource()
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var entered = Signal();
        var cleaning = Signal();
        var release = Signal();
        var cleaned = false;
        ((IPluginTaskActionApi)scope).Register([new("run", "Run")], async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cleaning.TrySetResult(); await release.Task; cleaned = true; }
            return PluginTaskActionResult.Success();
        });
        Create(fixture, "cooperative");
        fixture.Tasks.RunNow("cooperative");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var source = Source(Running(fixture.Tasks)["cooperative"]!);
        Task? stopping = null;
        try
        {
            stopping = fixture.Tasks.StopAsync(CancellationToken.None);
            await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            Assert.True(source.Token.IsCancellationRequested);
            Assert.False(cleaned);
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(cleaned);
            Assert.Throws<ObjectDisposedException>(() => source.Token);
            Assert.Empty(Running(fixture.Tasks).Keys.Cast<object>());
        }
        finally { release.TrySetResult(); if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedBudgetReturnsWithoutClaimingUncooperativeActionWasReleased(bool alreadyExpired)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        ((IPluginTaskActionApi)scope).Register([new("run", "Run")], async (_, _, _) =>
        {
            calls++;
            entered.TrySetResult();
            await release.Task;
            return PluginTaskActionResult.Success();
        });
        Create(fixture, "uncooperative");
        fixture.Tasks.RunNow("uncooperative");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var record = Running(fixture.Tasks)["uncooperative"]!;
        using var budget = new CancellationTokenSource();
        try
        {
            if (alreadyExpired) budget.Cancel();
            var stopping = fixture.Tasks.StopAsync(budget.Token);
            Assert.True(Source(record).Token.IsCancellationRequested);
            budget.Cancel();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Execution(record).IsCompleted);
            Assert.Single(Running(fixture.Tasks).Keys.Cast<object>());
            Assert.Contains(fixture.Logger.Messages, message => message.Contains("未结束，继续退出"));
            fixture.Tasks.RunNow("uncooperative");
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); await Execution(record).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task RunRegisteredBeforeExecutionCannotBeMissedByStop()
    {
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var logger = new StartLogger(entered, release);
        await using var fixture = new TaskServiceFixture(logger: logger);
        fixture.Create("race");
        Task? run = null;
        Task? stopping = null;
        try
        {
            run = Task.Run(() => fixture.Tasks.RunNow("race"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(Running(fixture.Tasks).Keys.Cast<object>());
            stopping = fixture.Tasks.StopAsync(CancellationToken.None);
            Assert.False(stopping.IsCompleted);
            release.Set();
            await Task.WhenAll(run, stopping).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, fixture.Programs.Launches);
            Assert.Empty(Running(fixture.Tasks).Keys.Cast<object>());
            fixture.Tasks.RunNow("race");
            Assert.Equal(0, fixture.Programs.Launches);
        }
        finally { release.Set(); await Task.WhenAll(run ?? Task.CompletedTask, stopping ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task RenameAndOldCompletionDoNotRemoveNewRunOrAllowReentry()
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var releases = new Dictionary<string, TaskCompletionSource> { ["old"] = Signal(), ["new"] = Signal() };
        var calls = 0;
        ((IPluginTaskActionApi)scope).Register([new("run", "Run")], async (_, context, _) =>
        {
            calls++;
            await releases[context.TaskName!].Task;
            return PluginTaskActionResult.Success();
        });
        Create(fixture, "old");
        fixture.Tasks.RunNow("old");
        var previous = Running(fixture.Tasks)["old"]!;
        var oldToken = Source(previous).Token;
        try
        {
            fixture.Tasks.Update("old", fixture.Tasks.GetAll().Single() with { Name = "new" });
            Assert.True(oldToken.IsCancellationRequested);
            fixture.Tasks.RunNow("new");
            var current = Running(fixture.Tasks)["new"]!;
            fixture.Tasks.RunNow("new");
            Assert.Equal(2, calls);
            releases["old"].TrySetResult();
            await Execution(previous).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(current, Running(fixture.Tasks)["new"]);
            Assert.False(Source(current).Token.IsCancellationRequested);
            releases["new"].TrySetResult();
            await Execution(current).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(Running(fixture.Tasks).Keys.Cast<object>());
        }
        finally
        {
            foreach (var release in releases.Values) release.TrySetResult();
            await fixture.Tasks.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task HostStoppingCancelsTasksBeforeOtherServiceConsumesSharedBudget()
    {
        var actionEntered = Signal();
        var release = Signal();
        var otherEntered = Signal();
        TaskServiceFixture? fixture = null;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(provider => (fixture = new TaskServiceFixture(lifetime: provider.GetRequiredService<IHostApplicationLifetime>())).Tasks);
        builder.Services.AddHostedService(provider => provider.GetRequiredService<TaskService>());
        builder.Services.AddSingleton(new OtherService(otherEntered));
        builder.Services.AddHostedService(provider => provider.GetRequiredService<OtherService>());
        var host = builder.Build();
        try
        {
            await host.StartAsync();
            Assert.Equal(TimeSpan.FromSeconds(30), host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
            using var scope = fixture!.Extensions.CreateScope("test", "Test");
            ((IPluginTaskActionApi)scope).Register([new("run", "Run")], async (_, _, _) =>
            { actionEntered.TrySetResult(); await release.Task; return PluginTaskActionResult.Success(); });
            Create(fixture, "budget");
            fixture.Tasks.RunNow("budget");
            await actionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var record = Running(fixture.Tasks)["budget"]!;
            using var budget = new CancellationTokenSource();
            var stopping = host.StopAsync(budget.Token);
            await otherEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Source(record).Token.IsCancellationRequested);
            Assert.False(Execution(record).IsCompleted);
            budget.Cancel();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Execution(record).IsCompleted);
            release.TrySetResult();
            await Execution(record).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await host.StopAsync(new CancellationToken(true));
            await ((IAsyncDisposable)host).DisposeAsync();
            if (fixture is not null) await fixture.DisposeAsync();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Create(TaskServiceFixture fixture, string name) => fixture.Tasks.Create(new(name, false,
        new(TaskTriggerType.WinPilotStartup), [new(TaskActionType.Extension, PluginId: "test", ActionId: "run")]));
    private static IDictionary Running(TaskService service) => (IDictionary)typeof(TaskService).GetField("runningTasks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
    private static CancellationTokenSource Source(object record) => (CancellationTokenSource)record.GetType().GetProperty("Cancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(record)!;
    private static Task Execution(object record) => (Task)record.GetType().GetProperty("Execution", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(record)!;
    private sealed class StartLogger(TaskCompletionSource entered, ManualResetEventSlim release) : ILogger<TaskService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
        {
            if (format(state, exception) != "任务「race」开始执行，触发：手动运行") return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("未解除任务启动握手");
        }
    }
    private sealed class OtherService(TaskCompletionSource entered) : IHostedService
    {
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public async Task StopAsync(CancellationToken token)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }
}
