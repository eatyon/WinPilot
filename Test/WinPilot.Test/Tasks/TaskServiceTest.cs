using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Core.Notifications;
using WinPilot.Core.Tasks;
using WinPilot.Features.Notifications;
using WinPilot.Features.Tasks;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class TaskServiceTest
{
    [Theory]
    [InlineData(TaskActionType.Keyboard, null, "发送按键「F11」")]
    [InlineData(TaskActionType.Keyboard, "Ctrl", "发送按键「Ctrl + F11」")]
    [InlineData(TaskActionType.SendNotification, null, "发送「已启用渠道」通知")]
    [InlineData(TaskActionType.SendNotification, "QQBot", "发送「QQBot」通知")]
    [InlineData(TaskActionType.Extension, "test", "插件扩展「测试插件」")]
    [InlineData(TaskActionType.Extension, "missing", "插件扩展「missing」")]
    public async Task ActionLogsIncludeKeysChannelOrPluginName(TaskActionType type, string? value, string description)
    {
        await using var fixture = new TaskServiceFixture(new RecordingNotifications());
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        ((WinPilot.PluginContract.IPluginTaskActionApi)scope).Register([new("run", "测试动作")],
            (_, _, _) => Task.FromResult(WinPilot.PluginContract.PluginTaskActionResult.Success()));
        var action = type switch
        {
            TaskActionType.Keyboard => new TaskAction(type, Keys: value is null ? ["F11"] : [value, "F11"]),
            TaskActionType.SendNotification => new TaskAction(type, NotificationContent: "测试通知", NotificationChannel: value),
            _ => new TaskAction(type, PluginId: value, ActionId: "run", ContinueOnFailure: true)
        };
        fixture.Tasks.Create(new("test", true, new(TaskTriggerType.WinPilotStartup),
            [action, new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
        fixture.Tasks.RunNow("test");
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains($"任务「test」第 1 个动作开始：{description}", fixture.Logger.Messages);
        if (type == TaskActionType.Extension && value == "missing")
            Assert.Contains($"任务「test」第 1 个动作失败：{description}，继续执行后续动作", fixture.Logger.Messages);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("QQBot", true)]
    public async Task NotificationActionUsesSelectedChannelAndPersistsIt(string? channel, bool ignoreEnabled)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        fixture.Tasks.Create(new("Notification", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.SendNotification, NotificationContent: "测试通知", NotificationChannel: channel)]));
        Assert.Contains("测试通知", File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Notification.json")));
        var saved = Assert.Single(fixture.Tasks.GetAll()).Actions.Single();
        Assert.Equal(TaskActionType.SendNotification, saved.Type);
        Assert.Equal(channel ?? "", saved.NotificationChannel);
        var persisted = TaskActionPersistence.From(saved);
        Assert.Equal(string.IsNullOrWhiteSpace(channel) ? null : channel, persisted.NotificationChannel);
        var restored = persisted.ToAction();
        Assert.Equal(TaskActionType.SendNotification, restored.Type);
        Assert.Equal(persisted.NotificationChannel, restored.NotificationChannel);
        fixture.Tasks.RunNow("Notification");
        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("测试通知", sent.Message.Content);
        Assert.Equal(saved.NotificationChannel, sent.Channel);
        Assert.Equal(ignoreEnabled, sent.IgnoreEnabled);
    }

    [Fact]
    public async Task TaskApiReturnsOnlyFieldsForTheSelectedActionType()
    {
        await using var fixture = new TaskServiceFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Tasks);
        builder.Services.AddSingleton(fixture.Extensions);
        builder.Services.AddSingleton<TaskVariableCatalog>();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        await using var app = builder.Build();
        app.MapTaskEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var task = new
        {
            name = "Sparse", enabled = true, trigger = new { type = "WinPilotStartup" },
            actions = new[] { new { type = "StartProgram", programPath = "app.exe" } }
        };
        using var created = await client.PostAsJsonAsync("/api/tasks", task);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/tasks/Sparse", created.Headers.Location!.OriginalString);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.False(createdJson.RootElement.GetProperty("actions")[0].TryGetProperty("delaySeconds", out _));
        using var listed = JsonDocument.Parse(await client.GetStringAsync("/api/tasks"));
        Assert.False(listed.RootElement[0].GetProperty("actions")[0].TryGetProperty("delaySeconds", out _));
        using var updated = await client.PutAsJsonAsync("/api/tasks/Sparse", new
        {
            task.name, task.enabled, task.trigger,
            actions = new[] { new { type = "Delay", delaySeconds = 5 } }
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        var action = updatedJson.RootElement.GetProperty("actions")[0];
        Assert.Equal(5, action.GetProperty("delaySeconds").GetInt32());
        Assert.False(action.TryGetProperty("programPath", out _));
        using var toggled = await client.PatchAsJsonAsync("/api/tasks/Sparse/enabled", new { enabled = false });
        using var toggledJson = JsonDocument.Parse(await toggled.Content.ReadAsStringAsync());
        Assert.False(toggledJson.RootElement.GetProperty("actions")[0].TryGetProperty("force", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DelayMustBePositive(int seconds)
    {
        await using var fixture = new TaskServiceFixture();
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Tasks.Create(new(
            "Delay", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Delay, DelaySeconds: seconds)])));

        Assert.Equal("请输入有效的延迟秒数", error.Message);
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseProcess_CurrentUserOnlyFiltersOwnerAndProcessName(bool force)
    {
        await using var fixture = new TaskServiceFixture();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        fixture.Programs.RunningPrograms = [
            new(101, "APP.exe", identity.Name, 1),
            new(102, "app.exe", "OtherUser", 2),
            new(103, "other.exe", identity.Name, 1)
        ];
        fixture.Tasks.Create(new("Close", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.CloseProcess, ProcessName: "app.exe", Force: force, CurrentUserOnly: true)]));

        fixture.Tasks.RunNow("Close");
        var closed = await fixture.Programs.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(identity.Name, fixture.Programs.QueriedUserName);
        Assert.Equal(new[] { 101 }, closed.Ids);
        Assert.Equal(force, closed.Force);
    }

    [Fact]
    public async Task RunningTaskCannotReenterAndCanRunAgainAfterCompletion()
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        ((WinPilot.PluginContract.IPluginTaskActionApi)scope).Register([new("block", "Block")], async (_, _, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return WinPilot.PluginContract.PluginTaskActionResult.Success();
        });
        fixture.Tasks.Create(new("Example", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Extension, PluginId: "test", ActionId: "block"), new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
        fixture.Tasks.RunNow("Example");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Tasks.RunNow("Example");
        Assert.Equal(1, calls);
        release.SetResult();
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref fixture.Programs.Launches) < 2)
        {
            timeout.Token.ThrowIfCancellationRequested();
            fixture.Tasks.RunNow("Example");
            if (Volatile.Read(ref fixture.Programs.Launches) < 2) await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrDisposeCancelsRunsAndPreventsLaterSynchronousActions(bool dispose)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        ((WinPilot.PluginContract.IPluginTaskActionApi)scope).Register([new("block", "Block")], async (_, _, token) =>
        {
            operationToken = token;
            started.SetResult();
            await release.Task;
            return WinPilot.PluginContract.PluginTaskActionResult.Success();
        });
        fixture.Tasks.Create(new("Example", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Extension, PluginId: "test", ActionId: "block"), new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
        fixture.Tasks.RunNow("Example");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) fixture.Tasks.Dispose();
        else await fixture.Tasks.StopAsync(CancellationToken.None);
        Assert.True(operationToken.IsCancellationRequested);
        fixture.Logger.ActionCancelled = true;
        release.SetResult();
        await fixture.Logger.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Tasks.RunNow("Example");
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rename")]
    public async Task ExistingUnloadedFilePreventsCreateAndRename(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        var original = Assert.Single(fixture.Tasks.GetAll());
        var originalFile = Path.Combine(fixture.Paths.TasksDirectory, "Example.json");
        var originalContent = File.ReadAllText(originalFile);
        var target = Path.Combine(fixture.Paths.TasksDirectory, "Unloaded.json");
        const string invalidContent = "invalid task";
        File.WriteAllText(target, invalidContent);
        var replacement = original with { Name = "Unloaded" };

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            if (operation == "create") fixture.Tasks.Create(replacement);
            else fixture.Tasks.Update(original.Name, replacement);
        });

        Assert.Equal("同名任务文件已存在", exception.Message);
        Assert.Equal(original, Assert.Single(fixture.Tasks.GetAll()));
        Assert.Equal(originalContent, File.ReadAllText(originalFile));
        Assert.Equal(invalidContent, File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(fixture.Paths.TasksDirectory, "*.tmp"));
    }

    [Fact]
    public async Task FailedRenameSerializationRestoresOriginalFileAndMemory()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        var original = Assert.Single(fixture.Tasks.GetAll());
        var file = Path.Combine(fixture.Paths.TasksDirectory, "Example.json");
        var content = File.ReadAllText(file);
        var parameters = new Dictionary<string, object?>();
        parameters["cycle"] = parameters;
        var replacement = original with
        {
            Name = "Renamed",
            Trigger = new(TaskTriggerType.Extension, PluginId: "test", TriggerId: "trigger", Parameters: parameters)
        };

        Assert.Throws<System.Text.Json.JsonException>(() => fixture.Tasks.Update(original.Name, replacement));
        Assert.Equal(original, Assert.Single(fixture.Tasks.GetAll()));
        Assert.Equal(content, File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.TasksDirectory, "Renamed.json")));
        Assert.Empty(Directory.GetFiles(fixture.Paths.TasksDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("rename")]
    public async Task FailedPersistencePreservesTaskAndRemovesTemporaryFiles(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        var original = Assert.Single(fixture.Tasks.GetAll());
        var file = Path.Combine(fixture.Paths.TasksDirectory, "Example.json");
        var content = File.ReadAllText(file);
        var target = Path.Combine(fixture.Paths.TasksDirectory, "Blocked.json");
        Directory.CreateDirectory(target);
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var exception = Record.Exception(() =>
            {
                switch (operation)
                {
                    case "create": fixture.Tasks.Create(original with { Name = "Blocked" }); break;
                    case "update": fixture.Tasks.Update(original.Name, original with { Enabled = false }); break;
                    case "disable": fixture.Tasks.SetEnabled(original.Name, false); break;
                    case "delete": fixture.Tasks.Delete(original.Name); break;
                    case "rename": fixture.Tasks.Update(original.Name, original with { Name = "Blocked" }); break;
                }
            });
            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.Equal(original, Assert.Single(fixture.Tasks.GetAll()));
        }
        Assert.Equal(content, File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(fixture.Paths.TasksDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("stop")]
    public async Task ChangingTaskLifecycleCancelsRunningActionAndSkipsRemainingActions(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((WinPilot.PluginContract.IPluginTaskActionApi)scope).Register([new("block", "Block")], async (_, _, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                fixture.Logger.ActionCancelled = true;
                throw;
            }
            return WinPilot.PluginContract.PluginTaskActionResult.Success();
        });
        var task = new WinPilotTask("Example", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Extension, PluginId: "test", ActionId: "block"),
             new(TaskActionType.StartProgram, ProgramPath: "app.exe")]);
        fixture.Tasks.Create(task);
        fixture.Tasks.RunNow(task.Name);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (operation)
        {
            case "disable": fixture.Tasks.SetEnabled(task.Name, false); break;
            case "delete": fixture.Tasks.Delete(task.Name); break;
            case "rename": fixture.Tasks.Update(task.Name, task with { Name = "Renamed" }); break;
            case "stop": await fixture.Tasks.StopAsync(CancellationToken.None); break;
        }
        await fixture.Logger.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Programs.Launches);
        if (operation == "delete") Assert.Empty(fixture.Tasks.GetAll());
        if (operation == "disable") Assert.False(Assert.Single(fixture.Tasks.GetAll()).Enabled);
        if (operation == "rename") Assert.Equal("Renamed", Assert.Single(fixture.Tasks.GetAll()).Name);
    }

    [Fact]
    public async Task CronFiresAtDueTimeOnceAndSkipsDisabledTasks()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new("enabled", true, new(TaskTriggerType.Cron, Cron: "* * * * *"),
            [new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
        fixture.Tasks.Create(new("disabled", false, new(TaskTriggerType.Cron, Cron: "* * * * *"),
            [new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
        var time = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2030, 1, 15)));
        fixture.Tasks.PollCron(time, CancellationToken.None);
        fixture.Tasks.PollCron(time.AddSeconds(59), CancellationToken.None);
        Assert.Equal(0, fixture.Programs.Launches);
        fixture.Tasks.PollCron(time.AddMinutes(1), CancellationToken.None);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Tasks.PollCron(time.AddMinutes(1), CancellationToken.None);
        Assert.Equal(1, fixture.Programs.Launches);
        fixture.Tasks.PollCron(time.AddMinutes(2), CancellationToken.None);
        Assert.Equal(2, fixture.Programs.Launches);
    }

    [Theory]
    [InlineData("app", "C:\\tools\\app.exe", true)]
    [InlineData("APP", "c:\\TOOLS\\APP.exe", true)]
    [InlineData("app", "C:\\other\\app.exe", false)]
    [InlineData("other", "C:\\tools\\app.exe", false)]
    [InlineData("app", "", false)]
    public void ProcessTriggerRequiresBothNameAndConfiguredPath(string name, string path, bool matches)
        => Assert.Equal(matches, TaskService.Matches(["C:\\tools\\app.exe"], name, path));

    [Theory]
    [InlineData(TaskTriggerType.Cron, "invalid", "")]
    [InlineData(TaskTriggerType.Cron, "", "")]
    [InlineData(TaskTriggerType.ProcessStarted, "", "relative.exe")]
    [InlineData(TaskTriggerType.ProcessExited, "", "")]
    public async Task InvalidAutomaticTriggersAreRejectedWithoutSaving(TaskTriggerType type, string cron, string processPath)
    {
        await using var fixture = new TaskServiceFixture();
        Assert.Throws<InvalidOperationException>(() => fixture.Tasks.Create(new("invalid", true,
            new(type, Cron: cron, ProcessPaths: string.IsNullOrEmpty(processPath) ? [] : [processPath]),
            [new(TaskActionType.StartProgram, ProgramPath: "app.exe")])));
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Fact]
    public async Task AutomaticStartupStillSkipsDisabledTasksWhileManualRunAllowsThem()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("已关闭", false);
        fixture.Create("已开启");
        fixture.Tasks.NotifyPluginsLoaded();
        await fixture.Tasks.StartAsync(CancellationToken.None);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Programs.Launches);
        fixture.Tasks.RunNow("已关闭");
        Assert.Equal(2, fixture.Programs.Launches);
        Assert.False(fixture.Tasks.GetAll().Single(task => task.Name == "已关闭").Enabled);
    }

    private sealed class RecordingNotifications : INotificationService
    {
        internal TaskCompletionSource<(NotificationMessage Message, string? Channel, bool IgnoreEnabled)> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default, string? channel = null, bool ignoreChannelEnabled = false)
        {
            Sent.TrySetResult((message, channel, ignoreChannelEnabled));
            return Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new("Test", true)]);
        }
    }
}
