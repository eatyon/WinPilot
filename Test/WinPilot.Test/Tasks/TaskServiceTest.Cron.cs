using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cronos;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    [Theory]
    [InlineData("0 */5 * * * *")]
    [InlineData("*/5 * * * * *")]
    [InlineData("* * * * * *")]
    [InlineData("0 0 8 L * MON-FRI")]
    [InlineData("0 0 8 * * 7")]
    [InlineData("0 0 8 * * 1-7")]
    [InlineData("0 0 0 31 2 *")]
    public async Task CronPreviewUsesSchedulerRulesAndLocalTimeWithoutSavingTasks(string expression)
    {
        await using var fixture = new TaskServiceFixture();
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        var before = DateTimeOffset.Now;
        using var response = await client.GetAsync("/api/tasks/cron/preview?expression=" + Uri.EscapeDataString(expression));
        var after = DateTimeOffset.Now;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var times = json.RootElement.GetProperty("nextRuns").EnumerateArray().Select(value => value.GetDateTimeOffset()).ToArray();
        var cron = CronExpression.Parse(expression, CronFormat.IncludeSeconds);
        if (expression == "0 0 0 31 2 *")
        {
            Assert.Empty(times);
        }
        else
        {
            Assert.Equal(5, times.Length);
            Assert.InRange(
                times[0],
                cron.GetNextOccurrence(before, TimeZoneInfo.Local)!.Value,
                cron.GetNextOccurrence(after, TimeZoneInfo.Local)!.Value);
            for (var index = 0; index < times.Length; index++)
            {
                Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(times[index]), times[index].Offset);
                if (index > 0)
                {
                    Assert.Equal(cron.GetNextOccurrence(times[index - 1], TimeZoneInfo.Local), times[index]);
                }
            }
        }
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData(null, "请输入 Cron 表达式")]
    [InlineData("", "请输入 Cron 表达式")]
    [InlineData(" ", "请输入 Cron 表达式")]
    [InlineData("invalid", "Cron 表达式格式无效")]
    [InlineData("0 60 * * * *", "Cron 表达式格式无效")]
    [InlineData("0 0 0 8 * * *", "Cron 表达式格式无效")]
    [InlineData("0 8 * * *", "Cron 表达式格式无效")]
    public async Task CronPreviewReturnsFixedValidationErrors(string? expression, string expected)
    {
        await using var fixture = new TaskServiceFixture();
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/tasks/cron/preview" +
            (expression is null ? "" : "?expression=" + Uri.EscapeDataString(expression)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("detail").GetString());
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData("0 0/5 * * * *")]
    [InlineData("0/5 * * * * *")]
    [InlineData("0 0 8-18 1,15 * 1-5")]
    [InlineData("0 0 8 1/3 1/2 6,7")]
    [InlineData("0 0 8 * * 0-6")]
    [InlineData("0 0 8 * * 0/2")]
    [InlineData("0 0 8 * * 0,6")]
    [InlineData("0 0 8 * * 1-7")]
    [InlineData("0 0 8 * * 1/2")]
    [InlineData("0 0 8 * * 7")]
    [InlineData("0 0 8 L * MON-FRI")]
    public async Task SixFieldCronCanBeSavedWithoutChangingTheExpression(string expression)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new(
            "generated",
            false,
            new(TaskTriggerType.Cron, Cron: expression),
            [new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "content")]));
        Assert.Equal(expression, Assert.Single(fixture.Tasks.GetAll()).Trigger.Cron);
    }

    [Fact]
    public async Task CronFiresAtDueTimeOnceAndSkipsDisabledTasks()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new(
            "enabled",
            true,
            new(TaskTriggerType.Cron, Cron: "0 * * * * *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.Create(new(
            "disabled",
            false,
            new(TaskTriggerType.Cron, Cron: "0 * * * * *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var time = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2030, 1, 15)));
        await fixture.ProcessDueCronTasksAsync(time, CancellationToken.None);
        await fixture.ProcessDueCronTasksAsync(time.AddSeconds(59), CancellationToken.None);
        Assert.Equal(0, fixture.Programs.Launches);
        await fixture.ProcessDueCronTasksAsync(time.AddMinutes(1), CancellationToken.None);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ProcessDueCronTasksAsync(time.AddMinutes(1), CancellationToken.None);
        Assert.Equal(1, fixture.Programs.Launches);
        await fixture.ProcessDueCronTasksAsync(time.AddMinutes(2), CancellationToken.None);
        Assert.Equal(2, fixture.Programs.Launches);
    }

    [Fact]
    public async Task CronWithSecondsRunsConsecutiveSecondsWithoutSkippingTheNextOne()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new(
            "seconds",
            true,
            new(TaskTriggerType.Cron, Cron: "* * * * * *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var time = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await fixture.ProcessDueCronTasksAsync(time.AddMilliseconds(500), CancellationToken.None);
        Assert.Equal(0, fixture.Programs.Launches);
        for (var second = 1; second <= 5; second++)
        {
            await fixture.ProcessDueCronTasksAsync(time.AddSeconds(second), CancellationToken.None);
            Assert.Equal(second, fixture.Programs.Launches);
            await fixture.ProcessDueCronTasksAsync(time.AddSeconds(second), CancellationToken.None);
            Assert.Equal(second, fixture.Programs.Launches);
        }
    }

    [Fact]
    public async Task CronWithoutNextOccurrenceDoesNotBlockLaterDueTasks()
    {
        await using var fixture = new TaskServiceFixture();
        var time = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2030, 1, 15)));
        Assert.Null(CronExpression.Parse("0 0 0 31 2 *", CronFormat.IncludeSeconds).GetNextOccurrence(time, TimeZoneInfo.Local));
        fixture.Tasks.Create(new(
            "b-healthy",
            true,
            new(TaskTriggerType.Cron, Cron: "0 * * * * *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        await fixture.ProcessDueCronTasksAsync(time, CancellationToken.None);
        fixture.Tasks.Create(new(
            "a-unreachable",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        Assert.Equal(new[] { "a-unreachable", "b-healthy" }, fixture.Tasks.GetAll().Select(task => task.Name));

        var failure = await Record.ExceptionAsync(() => fixture.ProcessDueCronTasksAsync(time.AddMinutes(1), CancellationToken.None));
        var launchesAfterFailure = fixture.Programs.Launches;
        await fixture.ProcessDueCronTasksAsync(time.AddMinutes(1), CancellationToken.None);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Programs.Launches);
        Assert.True(
            failure is null && launchesAfterFailure == 1,
            $"调度异常：{failure?.GetType().Name}：{failure?.Message}；后续到期任务执行次数：{launchesAfterFailure}；同一时刻再次检查执行次数：{fixture.Programs.Launches}");
    }

    [Fact]
    public async Task CronTaskFailureDoesNotBlockLaterDueTasks()
    {
        using var probe = new CronFailureProbeLogger();
        await using var fixture = new TaskServiceFixture(logger: probe);
        var time = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        fixture.Tasks.Create(new(
            "b-healthy",
            true,
            new(TaskTriggerType.Cron, Cron: "0 * * * * *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        await fixture.ProcessDueCronTasksAsync(time, CancellationToken.None);
        fixture.Tasks.Create(new(
            "a-unreachable",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var injected = 0;
        probe.BeforeLog = (level, message) =>
        {
            if (level == LogLevel.Warning && message.Contains("a-unreachable", StringComparison.Ordinal))
            {
                injected++;
                throw new InvalidOperationException("受控单任务异常");
            }
        };
        await fixture.ProcessDueCronTasksAsync(time.AddMinutes(1), CancellationToken.None);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ProcessDueCronTasksAsync(time.AddMinutes(1), CancellationToken.None);
        Assert.Equal(1, injected);
        Assert.Equal(1, fixture.Programs.Launches);
        Assert.Equal("受控单任务异常", Assert.Single(probe.Exceptions).Message);
    }

    [Fact]
    public async Task CronWithoutNextOccurrenceStopsSchedulingWithoutDisablingAndWarnsOnce()
    {
        using var probe = new CronFailureProbeLogger();
        await using var fixture = new TaskServiceFixture(logger: probe);
        fixture.Tasks.Create(new(
            "unreachable",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var file = Path.Combine(fixture.Paths.TasksDirectory, "unreachable.json");
        var original = File.ReadAllText(file);
        var time = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 10; index++)
        {
            await fixture.ProcessDueCronTasksAsync(time.AddMinutes(index), CancellationToken.None);
        }
        var warning = Assert.Single(probe.Warnings);
        Assert.Contains("任务「unreachable」", warning);
        Assert.Contains("0 0 0 31 2 *", warning);
        Assert.Contains("已停止该任务调度", warning);
        Assert.Empty(probe.Exceptions);
        Assert.Equal(0, fixture.Programs.Launches);
        Assert.True(Assert.Single(fixture.Tasks.GetAll()).Enabled);
        Assert.Equal(original, File.ReadAllText(file));
        using var stored = JsonDocument.Parse(File.ReadAllText(file));
        Assert.True(stored.RootElement.GetProperty("enabled").GetBoolean());
        await using var reloaded = new TaskServiceFixture(initialize: paths =>
        {
            Directory.CreateDirectory(paths.TasksDirectory);
            File.Copy(file, Path.Combine(paths.TasksDirectory, "unreachable.json"));
        });
        Assert.True(Assert.Single(reloaded.Tasks.GetAll()).Enabled);
        await reloaded.ProcessDueCronTasksAsync(time.AddDays(1), CancellationToken.None);
        Assert.Single(reloaded.Logger.Messages.Where(message => message.Contains("没有后续执行时间")));
    }

    [Fact]
    public async Task TaskListReportsCronAvailabilityWithoutPersistingItOrChangingScheduling()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new(
            "unreachable",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.Create(new(
            "disabled",
            false,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.Create(new(
            "healthy",
            true,
            new(TaskTriggerType.Cron, Cron: "0 * * * * *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.Create(new(
            "startup",
            true,
            new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var originals = Directory.GetFiles(fixture.Paths.TasksDirectory).ToDictionary(path => path, File.ReadAllText);
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync("/api/tasks");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var listed = json.RootElement.EnumerateArray().ToDictionary(task => task.GetProperty("name").GetString()!);
            Assert.False(listed["unreachable"].GetProperty("cronAvailable").GetBoolean());
            Assert.True(listed["unreachable"].GetProperty("enabled").GetBoolean());
            Assert.False(listed["disabled"].GetProperty("cronAvailable").GetBoolean());
            Assert.False(listed["disabled"].GetProperty("enabled").GetBoolean());
            Assert.True(listed["healthy"].GetProperty("cronAvailable").GetBoolean());
            Assert.False(listed["startup"].TryGetProperty("cronAvailable", out _));
        }
        Assert.Empty(fixture.Logger.Messages);
        Assert.Equal(0, fixture.Programs.Launches);
        foreach (var (path, content) in originals)
        {
            Assert.Equal(content, File.ReadAllText(path));
            using var stored = JsonDocument.Parse(content);
            Assert.False(stored.RootElement.TryGetProperty("cronAvailable", out _));
        }
    }

    [Fact]
    public async Task UnavailableCronStillAllowsManualExecutionAndClearsStatusAfterEditing()
    {
        await using var fixture = new TaskServiceFixture();
        var task = fixture.Tasks.Create(new(
            "unreachable",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        await fixture.ProcessDueCronTasksAsync(DateTimeOffset.Now, CancellationToken.None);
        Assert.False(fixture.Tasks.GetCronAvailability(task));
        fixture.Tasks.RunNow(task.Name);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Programs.Launches);
        var updated = fixture.Tasks.Update(task.Name, task with { Trigger = new(TaskTriggerType.Cron, Cron: "0 * * * * *") });
        Assert.True(fixture.Tasks.GetCronAvailability(updated));
        Assert.Null(fixture.Tasks.GetCronAvailability(task));
        Assert.True(updated.Enabled);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("reenable")]
    [InlineData("rename")]
    [InlineData("recreate")]
    public async Task ExhaustedCronIsRecheckedWhenEnabledThroughTaskChanges(string operation)
    {
        using var probe = new CronFailureProbeLogger();
        await using var fixture = new TaskServiceFixture(logger: probe);
        fixture.Tasks.Create(new(
            "unreachable",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var time = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await fixture.ProcessDueCronTasksAsync(time, CancellationToken.None);
        Assert.Single(probe.Warnings);
        Assert.True(Assert.Single(fixture.Tasks.GetAll()).Enabled);
        var task = Assert.Single(fixture.Tasks.GetAll()) with { Enabled = true };
        switch (operation)
        {
            case "edit":
                fixture.Tasks.Update(task.Name, task);
                break;
            case "reenable":
                fixture.Tasks.SetEnabled(task.Name, false);
                fixture.Tasks.SetEnabled(task.Name, true);
                break;
            case "rename":
                fixture.Tasks.Update(task.Name, task with { Name = "renamed" });
                break;
            case "recreate":
                fixture.Tasks.Delete(task.Name);
                fixture.Tasks.Create(task);
                break;
        }
        await fixture.ProcessDueCronTasksAsync(time.AddMinutes(1), CancellationToken.None);
        await fixture.ProcessDueCronTasksAsync(time.AddMinutes(2), CancellationToken.None);
        Assert.Equal(2, probe.Warnings.Count);
        Assert.Empty(probe.Exceptions);
        Assert.Equal(0, fixture.Programs.Launches);
        task = Assert.Single(fixture.Tasks.GetAll());
        fixture.Tasks.Update(task.Name, task with { Enabled = true, Trigger = new(TaskTriggerType.Cron, Cron: "0 * * * * *") });
        await fixture.ProcessDueCronTasksAsync(time, CancellationToken.None);
        await fixture.ProcessDueCronTasksAsync(time.AddMinutes(1), CancellationToken.None);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Programs.Launches);
    }

    [Fact]
    public async Task CronWithoutNextOccurrenceDoesNotWriteEvenWhenTheFileIsLocked()
    {
        using var probe = new CronFailureProbeLogger();
        await using var fixture = new TaskServiceFixture(logger: probe);
        var task = fixture.Tasks.Create(new(
            "unreachable",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var file = Path.Combine(fixture.Paths.TasksDirectory, "unreachable.json");
        var original = File.ReadAllText(file);
        var time = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await fixture.ProcessDueCronTasksAsync(time, CancellationToken.None);
        }
        for (var index = 1; index <= 5; index++)
        {
            await fixture.ProcessDueCronTasksAsync(time.AddMinutes(index), CancellationToken.None);
        }
        Assert.Contains("已停止该任务调度", Assert.Single(probe.Warnings));
        Assert.Empty(probe.Exceptions);
        Assert.Same(task, Assert.Single(fixture.Tasks.GetAll()));
        Assert.True(task.Enabled);
        Assert.Equal(original, File.ReadAllText(file));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TasksDirectory, "*.tmp"));
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CronLastDueOccurrenceRunsWithoutDisablingOrWriting(bool lockFile)
    {
        using var probe = new CronFailureProbeLogger();
        await using var fixture = new TaskServiceFixture(logger: probe);
        var expression = CronExpression.Parse("0 0 0 31 12 *", CronFormat.IncludeSeconds);
        var time = new DateTimeOffset(9999, 12, 30, 0, 0, 0, TimeSpan.Zero);
        var due = expression.GetNextOccurrence(time, TimeZoneInfo.Local);
        Assert.NotNull(due);
        Assert.Null(expression.GetNextOccurrence(due.Value.AddSeconds(1), TimeZoneInfo.Local));
        fixture.Tasks.Create(new(
            "last",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 12 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        await fixture.ProcessDueCronTasksAsync(time, CancellationToken.None);
        using var fileLock = lockFile
            ? File.Open(
                Path.Combine(fixture.Paths.TasksDirectory, "last.json"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read)
            : null;
        await fixture.ProcessDueCronTasksAsync(due.Value, CancellationToken.None);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ProcessDueCronTasksAsync(due.Value.AddSeconds(1), CancellationToken.None);
        Assert.Equal(1, fixture.Programs.Launches);
        Assert.Single(probe.Warnings);
        Assert.True(Assert.Single(fixture.Tasks.GetAll()).Enabled);
        Assert.Empty(probe.Exceptions);
    }

    [Fact]
    public async Task CronDueProcessingPropagatesServiceCancellationWithoutSchedulingTasks()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new(
            "enabled",
            true,
            new(TaskTriggerType.Cron, Cron: "0 * * * * *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var time = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await fixture.ProcessDueCronTasksAsync(time, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => fixture.Tasks.ProcessDueCronTasks(time.AddMinutes(1), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, fixture.Programs.Launches);
        Assert.Empty(fixture.Logger.Exceptions);
    }

    [Fact]
    public async Task CronSchedulingFailureKeepsRetryDelay()
    {
        using var probe = new CronFailureProbeLogger();
        await using var fixture = new TaskServiceFixture(logger: probe);
        fixture.Tasks.Create(new(
            "unreachable",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        probe.BeforeLog = (level, message) =>
        {
            if (level == LogLevel.Warning && message.Contains("没有后续执行时间", StringComparison.Ordinal))
            {
                fixture.Tasks.SetEnabled("unreachable", false);
                fixture.Tasks.SetEnabled("unreachable", true);
                throw new InvalidOperationException("受控单任务异常");
            }
            if (level == LogLevel.Error && message.Contains("Cron 调度失败", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("受控调度边界异常");
            }
        };
        fixture.Tasks.NotifyPluginsLoaded();
        try
        {
            await fixture.Tasks.StartAsync(CancellationToken.None);
            var observed = await probe.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(observed.Exceptions, exception =>
            {
                Assert.IsType<InvalidOperationException>(exception);
                Assert.Equal("受控调度边界异常", exception.Message);
            });
            Assert.True(
                observed.Interval >= TimeSpan.FromSeconds(1),
                $"两次任务监听失败的间隔：{observed.Interval.TotalMilliseconds:F3} ms，应保留至少 1000 ms 的异常重试延时");
        }
        finally
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stopping = fixture.Tasks.StopAsync(cancellation.Token);
            probe.Release();
            await stopping;
        }
    }

    [Fact]
    public async Task CronSchedulerStopsAfterWarningWithoutWaitingForAnotherDeadline()
    {
        using var probe = new CronFailureProbeLogger();
        await using var fixture = new TaskServiceFixture(logger: probe);
        var warned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.BeforeLog = (level, message) =>
        {
            if (level == LogLevel.Warning && message.Contains("没有后续执行时间", StringComparison.Ordinal))
            {
                warned.TrySetResult();
            }
        };
        fixture.Tasks.Create(new(
            "unreachable",
            true,
            new(TaskTriggerType.Cron, Cron: "0 0 0 31 2 *"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.NotifyPluginsLoaded();
        try
        {
            await fixture.Tasks.StartAsync(CancellationToken.None);
            await warned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await fixture.Tasks.StopAsync(cancellation.Token);
        }
        Assert.Single(probe.Warnings);
        Assert.Empty(probe.Exceptions);
        Assert.True(fixture.Tasks.ExecuteTask?.IsCompleted);
    }

    private sealed class CronFailureProbeLogger : ILogger<TaskService>, IDisposable
    {
        private readonly ManualResetEventSlim release = new(false);
        private readonly List<Exception> exceptions = [];
        private long firstFailure;

        internal Action<LogLevel, string>? BeforeLog { get; set; }
        internal System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();
        internal System.Collections.Concurrent.ConcurrentQueue<Exception> Exceptions { get; } = new();
        internal TaskCompletionSource<(TimeSpan Interval, Exception[] Exceptions)> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            BeforeLog?.Invoke(level, message);
            if (level == LogLevel.Warning)
            {
                Warnings.Enqueue(message);
            }
            if (exception is not null)
            {
                Exceptions.Enqueue(exception);
            }
            if (level != LogLevel.Error || exception is null || message != "任务监听失败" || exceptions.Count >= 2)
            {
                return;
            }
            var timestamp = Stopwatch.GetTimestamp();
            exceptions.Add(exception);
            if (exceptions.Count == 1)
            {
                firstFailure = timestamp;
            }
            else
            {
                Observed.TrySetResult((Stopwatch.GetElapsedTime(firstFailure, timestamp), exceptions.ToArray()));
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Cron 复现测试未释放日志握手");
                }
            }
        }

        internal void Release() => release.Set();

        public void Dispose() => release.Dispose();
    }
}
