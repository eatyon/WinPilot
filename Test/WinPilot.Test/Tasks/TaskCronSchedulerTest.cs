using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;
using Xunit.Abstractions;

namespace WinPilot.Test;

public sealed class TaskCronSchedulerTest(ITestOutputHelper output)
{
    [Fact]
    public async Task SchedulingSnapshotsOnlyEnabledCronTasksInNameOrder()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(Cron("z-last", "0 0 0 31 2 *"));
        fixture.Create("b-startup");
        fixture.Tasks.Create(Cron("a-first", "0 0 0 31 2 *"));
        fixture.Tasks.Create(Cron("c-disabled", "0 0 0 31 2 *") with { Enabled = false });
        await fixture.ProcessDueCronTasksAsync(DateTimeOffset.Now, CancellationToken.None);
        var warnings = fixture.Logger.Messages.Where(message => message.Contains("没有后续执行时间")).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains("a-first", warnings[0]);
        Assert.Contains("z-last", warnings[1]);
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BusyCronDiagnosticIsDebugOnlyAndLimitedToOncePerExecution(bool debugEnabled)
    {
        var logger = new CronSkipLogger(debugEnabled);
        await using var fixture = new TaskServiceFixture(logger: logger);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Programs.BeforeLaunch = _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        fixture.Tasks.Create(Cron("busy", "* * * * * *"));
        var now = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        fixture.Tasks.ProcessDueCronTasks(now, CancellationToken.None);
        for (var round = 0; round < 2; round++)
        {
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release.Reset();
            var execution = Task.Run(() => fixture.Tasks.RunNow("busy"));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                fixture.Tasks.RunNow("busy");
                Assert.Equal(debugEnabled ? round : 0, logger.Entries.Count);
                for (var second = 1; second <= 5; second++)
                {
                    fixture.Tasks.ProcessDueCronTasks(now.AddSeconds(round * 10 + second), CancellationToken.None);
                }
                Assert.Equal(debugEnabled ? round + 1 : 0, logger.Entries.Count);
                if (debugEnabled)
                {
                    var entry = logger.Entries.Last();
                    Assert.Contains("仍在运行", entry.Message);
                    Assert.Equal("busy", entry.Values.Single(pair => pair.Key == "Name").Value);
                    Assert.Equal(
                        now.AddSeconds(round == 0 ? 1 : 6),
                        Assert.IsType<DateTimeOffset>(entry.Values.Single(pair => pair.Key == "ScheduledAt").Value));
                }
            }
            finally
            {
                release.Set();
            }
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(round + 1, fixture.Programs.Launches);
        }
    }

    [Fact]
    public async Task SchedulingAndEnableChangesReuseTheParsedExpression()
    {
        await using var fixture = new TaskServiceFixture();
        var task = fixture.Tasks.Create(Cron("cached", "* * * * * *"));
        var parsed = Expressions(fixture.Tasks)[task.Name].Expression;
        var now = DateTimeOffset.Now;
        await fixture.ProcessDueCronTasksAsync(now, CancellationToken.None);
        await fixture.ProcessDueCronTasksAsync(now.AddSeconds(1), CancellationToken.None);
        Assert.Same(parsed, Expressions(fixture.Tasks)[task.Name].Expression);
        var disabled = fixture.Tasks.SetEnabled(task.Name, false);
        Assert.Same(disabled, Expressions(fixture.Tasks)[task.Name].Task);
        Assert.Same(parsed, Expressions(fixture.Tasks)[task.Name].Expression);
        var enabled = fixture.Tasks.SetEnabled(task.Name, true);
        Assert.Same(enabled, Expressions(fixture.Tasks)[task.Name].Task);
        Assert.Same(parsed, Expressions(fixture.Tasks)[task.Name].Expression);
    }

    [Fact]
    public async Task EditingRenamingAndDeletingKeepOnlyTheCurrentExpression()
    {
        await using var fixture = new TaskServiceFixture();
        var task = fixture.Tasks.Create(Cron("original", "* * * * * *"));
        var original = Expressions(fixture.Tasks)[task.Name].Expression;
        task = fixture.Tasks.Update(task.Name, Cron("renamed", "0 * * * * *"));
        var cached = Assert.Single(Expressions(fixture.Tasks));
        Assert.Equal("renamed", cached.Key);
        Assert.Same(task, cached.Value.Task);
        Assert.NotSame(original, cached.Value.Expression);
        var now = DateTimeOffset.Now;
        await fixture.ProcessDueCronTasksAsync(now, CancellationToken.None);
        await fixture.ProcessDueCronTasksAsync(now.AddMinutes(1), CancellationToken.None);
        Assert.Same(cached.Value.Expression, Expressions(fixture.Tasks)[task.Name].Expression);
        fixture.Tasks.Delete(task.Name);
        Assert.Empty(Expressions(fixture.Tasks));
    }

    [Fact]
    public async Task ChangingTriggerRemovesTheParsedExpression()
    {
        await using var fixture = new TaskServiceFixture();
        var task = fixture.Tasks.Create(Cron("changed", "* * * * * *"));
        fixture.Tasks.Update(task.Name, task with { Trigger = new(TaskTriggerType.WinPilotStartup) });
        Assert.Empty(Expressions(fixture.Tasks));
    }

    [Fact]
    public async Task FailedValidationAndRenameLeaveTheOriginalExpressionAndFile()
    {
        await using var fixture = new TaskServiceFixture();
        var task = fixture.Tasks.Create(Cron("original", "* * * * * *"));
        var parsed = Expressions(fixture.Tasks)[task.Name].Expression;
        var file = Path.Combine(fixture.Paths.TasksDirectory, "original.json");
        var content = File.ReadAllText(file);
        Assert.Throws<TaskValidationException>(() => fixture.Tasks.Update(task.Name, Cron(task.Name, "invalid")));
        Directory.CreateDirectory(Path.Combine(fixture.Paths.TasksDirectory, "blocked.json"));
        Assert.ThrowsAny<IOException>(() => fixture.Tasks.Update(task.Name, Cron("blocked", "0 * * * * *")));
        var cached = Assert.Single(Expressions(fixture.Tasks));
        Assert.Same(task, cached.Value.Task);
        Assert.Same(parsed, cached.Value.Expression);
        Assert.Same(task, Assert.Single(fixture.Tasks.GetAll()));
        Assert.Equal(content, File.ReadAllText(file));
    }

    [Fact]
    public async Task LoadingTasksCachesTheirValidatedExpressions()
    {
        await using var source = new TaskServiceFixture();
        source.Tasks.Create(Cron("loaded", "0 * * * * *") with { Enabled = false });
        await using var loaded = new TaskServiceFixture(initialize: paths =>
        {
            Directory.CreateDirectory(paths.TasksDirectory);
            File.Copy(
                Path.Combine(source.Paths.TasksDirectory, "loaded.json"),
                Path.Combine(paths.TasksDirectory, "loaded.json"));
        });
        var task = Assert.Single(loaded.Tasks.GetAll());
        var cached = Assert.Single(Expressions(loaded.Tasks));
        Assert.Same(task, cached.Value.Task);
        Assert.Equal(
            TaskCronExpression.Parse(task.Trigger.Cron!).GetNextOccurrence(DateTimeOffset.UnixEpoch, TimeZoneInfo.Local),
            cached.Value.Expression.GetNextOccurrence(DateTimeOffset.UnixEpoch, TimeZoneInfo.Local));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NextOccurrenceFailureWaitsOneSecondAndRecoversWithoutLosingTheDueRun(bool keepCachedDeadline)
    {
        var clock = new Clock();
        using var probe = new CalculationFailureLogger();
        await using var fixture = new TaskServiceFixture(clock: clock, logger: probe);
        var task = fixture.Tasks.Create(Cron("failure", "* * * * * *"));
        var file = Path.Combine(fixture.Paths.TasksDirectory, "failure.json");
        var originalFile = File.ReadAllText(file);
        try
        {
            await Start(fixture, clock);
            Assert.Equal(TimeSpan.FromMilliseconds(500), await clock.NextTimer());
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var gate = typeof(TaskService).GetField("gate", flags)!.GetValue(fixture.Tasks)!;
            var deadlines = (Dictionary<string, DateTimeOffset?>)typeof(TaskService).GetField("nextCron", flags)!.GetValue(fixture.Tasks)!;
            DateTimeOffset? originalDeadline;
            lock (gate)
            {
                originalDeadline = deadlines[task.Name];
                Assert.NotNull(originalDeadline);
                SetCron(fixture.Tasks, task, "invalid");
                if (!keepCachedDeadline)
                {
                    deadlines.Remove(task.Name);
                }
            }
            var timersBeforeFailure = clock.TimerCreations;
            clock.Advance(TimeSpan.FromMilliseconds(500));
            var failureTime = clock.GetUtcNow();
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimer());
            Assert.Single(probe.Exceptions);
            Assert.Equal(timersBeforeFailure + 1, clock.TimerCreations);
            Assert.Equal(1, clock.ActiveTimers);
            lock (gate)
            {
                if (keepCachedDeadline)
                {
                    Assert.Equal(originalDeadline, deadlines[task.Name]);
                }
                else
                {
                    Assert.False(deadlines.ContainsKey(task.Name));
                }
            }
            Assert.All(probe.Exceptions, exception => Assert.IsAssignableFrom<FormatException>(exception));
            Assert.Equal(failureTime, clock.GetUtcNow());
            Assert.Equal(0, fixture.Programs.Launches);
            Assert.Equal(originalFile, File.ReadAllText(file));
            clock.Advance(TimeSpan.FromMilliseconds(999));
            fixture.Tasks.ProcessDueCronTasks(clock.GetUtcNow(), CancellationToken.None);
            Assert.Single(probe.Exceptions);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimer());
            Assert.Equal(2, probe.Exceptions.Count);
            SetCron(fixture.Tasks, task, "* * * * * *");
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimer());
            if (keepCachedDeadline)
            {
                await Until(() => fixture.Programs.Launches == 1);
            }
            else
            {
                Assert.Equal(0, fixture.Programs.Launches);
            }
            Assert.Equal(0, RetryCount(fixture.Tasks));
            Assert.Equal(2, probe.Exceptions.Count);
            Assert.Equal(originalFile, File.ReadAllText(file));
            output.WriteLine($"过期缓存保留={keepCachedDeadline}；首次错误后等待一秒；提前检查未重复报错；恢复执行数={fixture.Programs.Launches}");
        }
        finally
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stopping = fixture.Tasks.StopAsync(budget.Token);
            probe.Release();
            await stopping;
        }
    }

    [Fact]
    public async Task AFailedTaskDoesNotRetryEarlyWhenAnotherTaskWakesTheScheduler()
    {
        var clock = new Clock();
        using var probe = new CalculationFailureLogger();
        await using var fixture = new TaskServiceFixture(clock: clock, logger: probe);
        var broken = fixture.Tasks.Create(Cron("a-failure", "* * * * * *"));
        fixture.Tasks.Create(Cron("b-healthy", "* * * * * *"));
        try
        {
            await Start(fixture, clock);
            await clock.NextTimer();
            SetCron(fixture.Tasks, broken, "invalid");
            clock.Advance(TimeSpan.FromMilliseconds(600));
            Assert.Equal(TimeSpan.FromMilliseconds(900), await clock.NextTimer());
            await Until(() => fixture.Programs.Launches == 1);
            Assert.Single(probe.Exceptions);
            clock.Advance(TimeSpan.FromMilliseconds(900));
            Assert.Equal(TimeSpan.FromMilliseconds(100), await clock.NextTimer());
            await Until(() => fixture.Programs.Launches == 2);
            Assert.Single(probe.Exceptions);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Equal(TimeSpan.FromMilliseconds(900), await clock.NextTimer());
            Assert.Equal(2, probe.Exceptions.Count);
        }
        finally
        {
            probe.Release();
        }
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("rename")]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("recreate")]
    public async Task TaskChangesClearTheFailedInstanceRetryState(string operation)
    {
        var clock = new Clock();
        using var probe = new CalculationFailureLogger();
        await using var fixture = new TaskServiceFixture(clock: clock, logger: probe);
        var task = fixture.Tasks.Create(Cron("one", "* * * * * *"));
        try
        {
            await Start(fixture, clock);
            await clock.NextTimer();
            SetCron(fixture.Tasks, task, "invalid");
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await clock.NextTimer();
            Assert.Equal(1, RetryCount(fixture.Tasks));
            SetCron(fixture.Tasks, task, "* * * * * *");
            var run = Run(fixture.Tasks)!;
            switch (operation)
            {
                case "edit":
                    fixture.Tasks.Update("one", Cron("one", "* * * * * *"));
                    break;
                case "rename":
                    fixture.Tasks.Update("one", Cron("renamed", "* * * * * *"));
                    break;
                case "disable":
                    fixture.Tasks.SetEnabled("one", false);
                    break;
                case "delete":
                    fixture.Tasks.Delete("one");
                    break;
                case "recreate":
                    fixture.Tasks.Delete("one");
                    fixture.Tasks.Create(Cron("one", "* * * * * *"));
                    break;
            }
            Assert.Equal(0, RetryCount(fixture.Tasks));
            if (operation is "disable" or "delete")
            {
                await Completion(run).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(Run(fixture.Tasks));
                Assert.Equal(0, clock.ActiveTimers);
            }
            else
            {
                await clock.NextTimer();
                clock.Advance(TimeSpan.FromSeconds(1));
                await Until(() => fixture.Programs.Launches == 1);
            }
            Assert.Single(probe.Exceptions);
        }
        finally
        {
            probe.Release();
        }
    }

    [Fact]
    public async Task StopDuringRetryReleasesTheWaitAndCancellationDoesNotBecomeAFailure()
    {
        var clock = new Clock();
        using var probe = new CalculationFailureLogger();
        await using var fixture = new TaskServiceFixture(clock: clock, logger: probe);
        var task = fixture.Tasks.Create(Cron("one", "* * * * * *"));
        try
        {
            await Start(fixture, clock);
            await clock.NextTimer();
            SetCron(fixture.Tasks, task, "invalid");
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await clock.NextTimer();
            await fixture.Tasks.StopAsync(CancellationToken.None);
            Assert.Equal(0, RetryCount(fixture.Tasks));
            Assert.Equal(0, clock.ActiveTimers);
            Assert.Null(Run(fixture.Tasks));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => fixture.Tasks.ProcessDueCronTasks(clock.GetUtcNow(), cancelled.Token));
            Assert.Single(probe.Exceptions);
        }
        finally
        {
            probe.Release();
        }
    }

    [Fact]
    public async Task AClockJumpDoesNotBypassTheFailedTasksOneSecondBackoff()
    {
        var clock = new Clock();
        using var probe = new CalculationFailureLogger();
        await using var fixture = new TaskServiceFixture(clock: clock, logger: probe);
        var task = fixture.Tasks.Create(Cron("one", "* * * * * *"));
        try
        {
            await Start(fixture, clock);
            await clock.NextTimer();
            SetCron(fixture.Tasks, task, "invalid");
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await clock.NextTimer();
            clock.SetUtc(clock.GetUtcNow().AddMinutes(30));
            fixture.Tasks.NotifyCronClockChanged();
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimer());
            Assert.Single(probe.Exceptions);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimer());
            Assert.Equal(2, probe.Exceptions.Count);
        }
        finally
        {
            probe.Release();
        }
    }

    [Fact]
    public async Task AnOldFailureCannotRestoreRetryStateAfterTheTaskHasBeenReplaced()
    {
        var clock = new Clock();
        using var probe = new CalculationFailureLogger();
        await using var fixture = new TaskServiceFixture(clock: clock, logger: probe);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = fixture.Tasks.Create(Cron("one", "* * * * * *"));
        try
        {
            await Start(fixture, clock);
            await clock.NextTimer();
            SetCron(fixture.Tasks, task, "invalid");
            probe.BeforeError = () =>
            {
                if (probe.Exceptions.Count != 1)
                {
                    return;
                }
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            };
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, RetryCount(fixture.Tasks));
            fixture.Tasks.Update("one", Cron("one", "* * * * * *"));
            Assert.Equal(0, RetryCount(fixture.Tasks));
            release.Set();
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimer());
            clock.Advance(TimeSpan.FromSeconds(1));
            await Until(() => fixture.Programs.Launches == 1);
            Assert.Single(probe.Exceptions);
            Assert.Equal(0, RetryCount(fixture.Tasks));
        }
        finally
        {
            release.Set();
            probe.Release();
        }
    }

    [Fact]
    public async Task CronResourcesExistOnlyWhileEnabledTasksHaveFutureOccurrences()
    {
        var clock = new Clock();
        await using var fixture = new TaskServiceFixture(clock: clock);
        await Start(fixture, clock);
        await Until(() => Ready(fixture.Tasks));
        Assert.Null(Run(fixture.Tasks));
        Assert.Equal(0, clock.ActiveTimers);
        fixture.Tasks.Create(Cron("one", "* * * * * *") with { Enabled = false });
        Assert.Null(Run(fixture.Tasks));
        fixture.Tasks.SetEnabled("one", true);
        Assert.Equal(TimeSpan.FromMilliseconds(500), await clock.NextTimer());
        var first = Run(fixture.Tasks)!;
        fixture.Tasks.SetEnabled("one", false);
        await Completion(first).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(Run(fixture.Tasks));
        Assert.Equal(0, clock.ActiveTimers);
        fixture.Tasks.SetEnabled("one", true);
        await clock.NextTimer();
        var second = Run(fixture.Tasks)!;
        Assert.NotSame(first, second);
        fixture.Tasks.Delete("one");
        await Completion(second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(Run(fixture.Tasks));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task ChangesWakeTheNearestDeadlineAndRetireUnreachableSchedules()
    {
        var clock = new Clock();
        await using var fixture = new TaskServiceFixture(clock: clock);
        fixture.Tasks.Create(Cron("daily", "0 0 0 * * *"));
        await Start(fixture, clock);
        Assert.True(await clock.NextTimer() > TimeSpan.FromHours(1));
        fixture.Tasks.Create(Cron("second", "* * * * * *"));
        Assert.Equal(TimeSpan.FromMilliseconds(500), await clock.NextTimer());
        fixture.Tasks.Update("second", Cron("renamed", "0 0 13 * * *"));
        Assert.Equal(TimeSpan.FromMinutes(60) - TimeSpan.FromMilliseconds(500), await clock.NextTimer());
        fixture.Tasks.Delete("renamed");
        Assert.True(await clock.NextTimer() > TimeSpan.FromHours(1));
        var run = Run(fixture.Tasks)!;
        fixture.Tasks.Delete("daily");
        await Completion(run).WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Tasks.Create(Cron("impossible", "0 0 0 31 2 *"));
        var unreachable = Run(fixture.Tasks);
        if (unreachable is not null)
        {
            await Completion(unreachable).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Null(Run(fixture.Tasks));
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Single(fixture.Logger.Messages.Where(message => message.Contains("没有后续执行时间")));
    }

    [Fact]
    public async Task SecondsUseAbsoluteDeadlinesAndMissedOccurrencesAreMerged()
    {
        var clock = new Clock();
        await using var fixture = new TaskServiceFixture(clock: clock);
        fixture.Tasks.Create(Cron("seconds", "* * * * * *"));
        await Start(fixture, clock);
        Assert.Equal(TimeSpan.FromMilliseconds(500), await clock.NextTimer());
        clock.Advance(TimeSpan.FromMilliseconds(700));
        Assert.Equal(TimeSpan.FromMilliseconds(800), await clock.NextTimer());
        await Until(() => fixture.Programs.Launches == 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(800), await clock.NextTimer());
        await Until(() => fixture.Programs.Launches == 2);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(800), await clock.NextTimer());
        await Until(() => fixture.Programs.Launches == 3);
        Assert.Equal(3, fixture.Programs.Launches);
    }

    [Fact]
    public async Task SynchronousActionsDoNotHoldTheSchedulerAndKeepReentryProtection()
    {
        var clock = new Clock();
        await using var fixture = new TaskServiceFixture(clock: clock);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        var blockedProgram = Path.Combine(fixture.Paths.ApplicationDirectory, "blocked.exe");
        File.WriteAllText(blockedProgram, "test program");
        fixture.Programs.BeforeLaunch = request =>
        {
            if (request.ProgramPath != blockedProgram)
            {
                return;
            }
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        fixture.Tasks.Create(Cron("blocked", "* * * * * *", blockedProgram));
        fixture.Tasks.Create(Cron("other", "* * * * * *"));
        try
        {
            await Start(fixture, clock);
            await clock.NextTimer();
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimer());
            await Until(() => fixture.Programs.Launches == 1);
            clock.Advance(TimeSpan.FromSeconds(1));
            await clock.NextTimer();
            await Until(() => fixture.Programs.Launches == 2);
            Assert.DoesNotContain(fixture.Programs.Requests, request => request.ProgramPath == blockedProgram);
            fixture.Tasks.SetEnabled("blocked", false);
        }
        finally
        {
            release.Set();
        }
        await Until(() => fixture.Programs.Launches == 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeZoneChangeReplacesThePendingDeadline(bool sameId)
    {
        var clock = new Clock();
        clock.SetTimeZone(TimeZoneInfo.CreateCustomTimeZone("old", TimeSpan.FromHours(8), "old", "old"));
        clock.SetUtc(DateTimeOffset.Parse("2030-01-15T00:30:00Z"));
        await using var fixture = new TaskServiceFixture(clock: clock);
        fixture.Tasks.Create(Cron("daily", "0 0 8 * * *"));
        var parsed = Expressions(fixture.Tasks)["daily"].Expression;
        await Start(fixture, clock);
        Assert.Equal(TimeSpan.FromHours(23.5) - TimeSpan.FromMilliseconds(500), await clock.NextTimer());
        clock.SetTimeZone(TimeZoneInfo.CreateCustomTimeZone(sameId ? "old" : "new", TimeSpan.FromHours(9), "new", "new"));
        fixture.Tasks.NotifyCronClockChanged();
        Assert.Equal(TimeSpan.FromHours(22.5) - TimeSpan.FromMilliseconds(500), await clock.NextTimer());
        Assert.Equal(0, fixture.Programs.Launches);
        Assert.Same(parsed, Expressions(fixture.Tasks)["daily"].Expression);
        clock.Advance(DateTimeOffset.Parse("2030-01-15T23:00:00Z") - clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromDays(1), await clock.NextTimer());
        await Until(() => fixture.Programs.Launches == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeZoneChangeDoesNotFireAnExpiredOldZoneDeadline(bool sameId)
    {
        var clock = new Clock();
        clock.SetTimeZone(TimeZoneInfo.CreateCustomTimeZone("old", TimeSpan.FromHours(8), "old", "old"));
        clock.SetUtc(DateTimeOffset.Parse("2030-01-15T00:30:00Z"));
        await using var fixture = new TaskServiceFixture(clock: clock);
        fixture.Tasks.Create(Cron("daily", "0 0 8 * * *"));
        await Start(fixture, clock);
        await clock.NextTimer();
        clock.SetUtc(DateTimeOffset.Parse("2030-01-16T00:01:00Z"));
        clock.SetTimeZone(TimeZoneInfo.CreateCustomTimeZone(sameId ? "old" : "new", TimeSpan.FromHours(7), "new", "new"));
        fixture.Tasks.NotifyCronClockChanged();
        Assert.Equal(TimeSpan.FromMinutes(59), await clock.NextTimer());
        Assert.Equal(0, fixture.Programs.Launches);
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(TimeSpan.FromDays(1), await clock.NextTimer());
        await Until(() => fixture.Programs.Launches == 1);
    }

    [Fact]
    public async Task TimeZoneChangeKeepsAnExhaustedTaskStoppedWithoutDisablingIt()
    {
        var clock = new Clock();
        clock.SetTimeZone(TimeZoneInfo.CreateCustomTimeZone("old", TimeSpan.FromHours(8), "old", "old"));
        await using var fixture = new TaskServiceFixture(clock: clock);
        fixture.Tasks.Create(Cron("unreachable", "0 0 0 31 2 *"));
        await fixture.ProcessDueCronTasksAsync(clock.GetUtcNow(), CancellationToken.None);
        clock.SetTimeZone(TimeZoneInfo.CreateCustomTimeZone("new", TimeSpan.FromHours(9), "new", "new"));
        await fixture.ProcessDueCronTasksAsync(clock.GetUtcNow(), CancellationToken.None);
        Assert.Single(fixture.Logger.Messages.Where(message => message.Contains("没有后续执行时间")));
        Assert.True(Assert.Single(fixture.Tasks.GetAll()).Enabled);
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Fact]
    public async Task EqualTimeZoneInstancesKeepTheOverdueRun()
    {
        var clock = new Clock();
        clock.SetTimeZone(TimeZoneInfo.CreateCustomTimeZone("local", TimeSpan.FromHours(8), "local", "local"));
        await using var fixture = new TaskServiceFixture(clock: clock);
        fixture.Tasks.Create(Cron("seconds", "* * * * * *"));
        await Start(fixture, clock);
        await clock.NextTimer();
        clock.SetUtc(clock.GetUtcNow().AddMinutes(3));
        clock.SetTimeZone(TimeZoneInfo.CreateCustomTimeZone("local", TimeSpan.FromHours(8), "local", "local"));
        fixture.Tasks.NotifyCronClockChanged();
        Assert.Equal(TimeSpan.FromMilliseconds(500), await clock.NextTimer());
        await Until(() => fixture.Programs.Launches == 1);
    }

    [Fact]
    public async Task ClockChangesWakeLongWaitsWithoutReplayingCompletedTimes()
    {
        var clock = new Clock();
        await using var fixture = new TaskServiceFixture(clock: clock);
        fixture.Tasks.Create(Cron("minute", "0 * * * * *"));
        await Start(fixture, clock);
        await clock.NextTimer();
        clock.SetUtc(clock.GetUtcNow().AddMinutes(3));
        fixture.Tasks.NotifyCronClockChanged();
        Assert.Equal(TimeSpan.FromSeconds(59.5), await clock.NextTimer());
        await Until(() => fixture.Programs.Launches == 1);
        clock.SetUtc(clock.GetUtcNow().AddMinutes(-2));
        fixture.Tasks.NotifyCronClockChanged();
        Assert.Equal(TimeSpan.FromSeconds(179.5), await clock.NextTimer());
        Assert.Equal(1, fixture.Programs.Launches);
    }

    [Fact]
    public async Task StopDisposesTheDeadlineAndPreventsSchedulerRestart()
    {
        var clock = new Clock();
        await using var fixture = new TaskServiceFixture(clock: clock);
        fixture.Tasks.Create(Cron("one", "0 0 0 * * *"));
        await Start(fixture, clock);
        await clock.NextTimer();
        var run = Run(fixture.Tasks)!;
        await fixture.Tasks.StopAsync(CancellationToken.None);
        Assert.True(Completion(run).IsCompletedSuccessfully);
        Assert.Equal(0, clock.ActiveTimers);
        fixture.Tasks.Create(Cron("late", "* * * * * *"));
        Assert.Null(Run(fixture.Tasks));
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Fact]
    public async Task DisableAndReenableDuringRetirementCannotLoseTheNewSchedule()
    {
        var clock = new Clock();
        await using var fixture = new TaskServiceFixture(clock: clock);
        fixture.Tasks.Create(Cron("one", "* * * * * *"));
        await Start(fixture, clock);
        await clock.NextTimer();
        for (int i = 0; i < 25; i++)
        {
            fixture.Tasks.SetEnabled("one", false);
            fixture.Tasks.SetEnabled("one", true);
        }
        await Until(() => clock.ActiveTimers == 1 && Run(fixture.Tasks) is not null);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => fixture.Programs.Launches == 1);
        Assert.NotNull(Run(fixture.Tasks));
    }

    [Fact]
    public async Task ReenableWhileTheOldWaitIsBeingDisposedCannotLoseItsWakeup()
    {
        var clock = new Clock();
        await using var fixture = new TaskServiceFixture(clock: clock);
        using var release = new ManualResetEventSlim();
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Tasks.Create(Cron("one", "* * * * * *"));
        await Start(fixture, clock);
        await clock.NextTimer();
        clock.BeforeDispose = () =>
        {
            disposing.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        try
        {
            fixture.Tasks.SetEnabled("one", false);
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Tasks.SetEnabled("one", true);
        }
        finally
        {
            clock.BeforeDispose = null;
            release.Set();
        }
        Assert.Equal(TimeSpan.FromMilliseconds(500), await clock.NextTimer());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Until(() => fixture.Programs.Launches == 1);
    }

    private static WinPilotTask Cron(string name, string expression, string? path = null) =>
        new(
            name,
            true,
            new(TaskTriggerType.Cron, Cron: expression),
            [new(TaskActionType.StartProgram, ProgramPath: path ?? Environment.ProcessPath!)]);

    private static async Task Start(TaskServiceFixture fixture, Clock clock)
    {
        fixture.Tasks.NotifyPluginsLoaded();
        await fixture.Tasks.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(500), await clock.NextTimer());
        clock.Advance(TimeSpan.FromMilliseconds(500));
    }

    private static object? Run(TaskService service) =>
        typeof(TaskService).GetField("cronSchedulerRun", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);

    private static int RetryCount(TaskService service)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var gate = typeof(TaskService).GetField("gate", flags)!.GetValue(service)!;
        lock (gate)
        {
            return ((Dictionary<string, long>)typeof(TaskService).GetField("cronRetryStartedAt", flags)!.GetValue(service)!).Count;
        }
    }

    private static void SetCron(TaskService service, WinPilotTask task, string expression)
    {
        var gate = typeof(TaskService).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        lock (gate)
        {
            typeof(TaskTrigger).GetProperty(nameof(TaskTrigger.Cron))!.SetValue(task.Trigger, expression);
            Expressions(service).Remove(task.Name);
        }
    }

    private static Dictionary<string, (WinPilotTask Task, Cronos.CronExpression Expression)> Expressions(TaskService service) =>
        (Dictionary<string, (WinPilotTask Task, Cronos.CronExpression Expression)>)typeof(TaskService)
            .GetField("cronExpressions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static bool Ready(TaskService service) =>
        (bool)typeof(TaskService).GetField("cronReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static Task Completion(object run) => (Task)run.GetType().GetProperty("Execution")!.GetValue(run)!;

    private static async Task Until(Func<bool> condition)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            budget.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset now = new DateTimeOffset(2030, 1, 15, 12, 0, 0,
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2030, 1, 15))).ToUniversalTime();
        private readonly List<Timer> timers = [];
        private readonly Channel<TimeSpan> created = Channel.CreateUnbounded<TimeSpan>();
        private long timestamp;
        private TimeZoneInfo timeZone = TimeZoneInfo.Local;
        internal Action? BeforeDispose;

        internal int TimerCreations { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return now;
            }
        }

        public override long GetTimestamp()
        {
            lock (gate)
            {
                return timestamp;
            }
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override TimeZoneInfo LocalTimeZone
        {
            get
            {
                lock (gate)
                {
                    return timeZone;
                }
            }
        }

        internal int ActiveTimers
        {
            get
            {
                lock (gate)
                {
                    return timers.Count;
                }
            }
        }

        internal async Task<TimeSpan> NextTimer() =>
            await created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            lock (gate)
            {
                var timer = new Timer(this, callback, state, now + dueTime);
                timers.Add(timer);
                TimerCreations++;
                created.Writer.TryWrite(dueTime);
                return timer;
            }
        }

        internal void SetUtc(DateTimeOffset value)
        {
            lock (gate)
            {
                now = value;
            }
        }

        internal void SetTimeZone(TimeZoneInfo value)
        {
            lock (gate)
            {
                timeZone = value;
            }
        }

        internal void Advance(TimeSpan amount)
        {
            Timer[] due;
            lock (gate)
            {
                now += amount;
                timestamp += amount.Ticks;
                due = timers.Where(timer => timer.Due <= now).ToArray();
                foreach (var timer in due)
                {
                    timers.Remove(timer);
                }
            }
            foreach (var timer in due)
            {
                timer.Callback(timer.State);
            }
        }

        private sealed class Timer(
            Clock clock,
            TimerCallback callback,
            object? state,
            DateTimeOffset due) : ITimer
        {
            internal DateTimeOffset Due = due;
            internal TimerCallback Callback = callback;
            internal object? State = state;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate)
                {
                    Due = clock.now + dueTime;
                    return true;
                }
            }

            public void Dispose()
            {
                clock.BeforeDispose?.Invoke();
                lock (clock.gate)
                {
                    clock.timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CronSkipLogger(bool debugEnabled) : ILogger<TaskService>
    {
        internal System.Collections.Concurrent.ConcurrentQueue<(string Message, KeyValuePair<string, object?>[] Values)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level != LogLevel.Debug || debugEnabled;

        public void Log<TState>(
            LogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Debug && debugEnabled)
            {
                Entries.Enqueue((formatter(state, exception), ((IEnumerable<KeyValuePair<string, object?>>)state!).ToArray()));
            }
        }
    }

    private sealed class CalculationFailureLogger : ILogger<TaskService>, IDisposable
    {
        private readonly ManualResetEventSlim release = new(false);

        internal System.Collections.Concurrent.ConcurrentQueue<Exception> Exceptions { get; } = new();

        internal Action? BeforeError;

        internal TaskCompletionSource ThirdFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level != LogLevel.Error || exception is null || !formatter(state, exception).Contains("Cron 调度失败"))
            {
                return;
            }
            Exceptions.Enqueue(exception);
            BeforeError?.Invoke();
            if (Exceptions.Count != 3)
            {
                return;
            }
            ThirdFailure.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("未释放 Cron 故障复现握手");
            }
        }

        internal void Release() => release.Set();

        public void Dispose() => release.Dispose();
    }
}
