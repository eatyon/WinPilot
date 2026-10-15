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
using WinPilot.PluginContract;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
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
    public async Task TaskExtensionApiReturnsRegisteredDefinitionsAndDynamicOptions()
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("example", "示例插件");
        scope.Register([new PluginTriggerDefinition("ready", "准备完成")]);
        ((IPluginTaskActionApi)scope).Register(
            [new PluginTaskActionDefinition("run", "运行动作", [new("target", "目标", PluginActionParameterType.Select,
                Options: [new("initial", "初始配置")])])],
            (_, _, _) => Task.FromResult(PluginTaskActionResult.Success()),
            (actionId, parameterKey, _) =>
            {
                Assert.Equal("run", actionId);
                Assert.Equal("target", parameterKey);
                return Task.FromResult<IReadOnlyList<PluginActionOption>>([new("saved", "已保存配置")]);
            });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Tasks);
        builder.Services.AddSingleton(fixture.Extensions);
        builder.Services.AddSingleton<TaskVariableCatalog>();
        await using var app = builder.Build();
        app.MapTaskEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var triggers = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/extensions/triggers"));
        Assert.Equal("ready", Assert.Single(triggers.RootElement.EnumerateArray()).GetProperty("triggerId").GetString());
        using var actions = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/extensions/actions"));
        var action = Assert.Single(actions.RootElement.EnumerateArray());
        Assert.Equal("run", action.GetProperty("actionId").GetString());
        Assert.Equal("示例插件", action.GetProperty("pluginDisplayName").GetString());
        Assert.True(action.GetProperty("dynamicOptions").GetBoolean());
        var options = await client.GetFromJsonAsync<PluginActionOption[]>("/api/tasks/extensions/actions/example/run/options/target");
        Assert.Equal(new PluginActionOption("saved", "已保存配置"), Assert.Single(options!));
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
    [InlineData(1, true)]
    [InlineData(86400, true)]
    [InlineData(86401, false)]
    [InlineData(int.MaxValue, false)]
    public async Task DelayCannotExceedOneDay(int seconds, bool valid)
    {
        await using var fixture = new TaskServiceFixture();
        var task = new WinPilotTask("Delay", false, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Delay, DelaySeconds: seconds)]);
        if (valid)
        {
            fixture.Tasks.Create(task);
            Assert.Equal(seconds, Assert.Single(fixture.Tasks.GetAll()).Actions[0].DelaySeconds);
        }
        else
        {
            var error = Assert.Throws<InvalidOperationException>(() => fixture.Tasks.Create(task));
            Assert.Equal("延迟时间不能超过 86400 秒（24 小时）", error.Message);
            Assert.Empty(fixture.Tasks.GetAll());
        }
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

    private sealed class RecordingNotifications(Func<CancellationToken, Task<IReadOnlyList<NotificationChannelResult>>>? send = null) : INotificationService
    {
        internal TaskCompletionSource<(NotificationMessage Message, string? Channel, bool IgnoreEnabled)> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? ImageBytes { get; private set; }
        internal CancellationToken Token { get; private set; }
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default, string? channel = null, bool ignoreChannelEnabled = false)
        {
            ImageBytes = message.ImagePath is null ? null : File.ReadAllBytes(message.ImagePath);
            Token = cancellationToken;
            Sent.TrySetResult((message, channel, ignoreChannelEnabled));
            return send?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new("Test", true)]);
        }
    }

    [Theory]
    [InlineData("测试通知", true)]
    [InlineData("", false)]
    public async Task NotificationEmptyImageVariableIsTreatedAsNoImage(string content, bool succeeds)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([], "image/png"));

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification,
            NotificationContent: string.IsNullOrEmpty(content) ? "&test.image" : $"{content}\n&test.image"), succeeds);

        if (succeeds)
        {
            var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(content, sent.Message.Content);
            Assert.Null(sent.Message.ImagePath);
            Assert.Null(notifications.ImageBytes);
        }
        else
        {
            Assert.False(notifications.Sent.Task.IsCompleted);
        }
    }

    [Theory]
    [InlineData(1, "image/png", ".png")]
    [InlineData(10 * 1024 * 1024, "image/png", ".png")]
    [InlineData(1, "IMAGE/JPEG", ".jpg")]
    [InlineData(10 * 1024 * 1024, "image/jpeg", ".jpg")]
    public async Task NotificationImageVariableAcceptsSizeBoundariesAndDeclaredFormat(int length, string contentType, string extension)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        var image = new byte[length];
        using var scope = RegisterNotificationImage(fixture, new(image, contentType));

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationContent: "&test.image"), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("", sent.Message.Content);
        Assert.Equal(extension, Path.GetExtension(sent.Message.ImagePath));
        Assert.Equal(image, notifications.ImageBytes);
        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Theory]
    [InlineData(10 * 1024 * 1024 + 1, "image/png", "通知图片不能超过 10 MB")]
    [InlineData(1, "image/gif", "通知图片仅支持 PNG 或 JPEG")]
    [InlineData(1, "image/webp", "通知图片仅支持 PNG 或 JPEG")]
    [InlineData(1, "image/jpg", "通知图片仅支持 PNG 或 JPEG")]
    public async Task NotificationImageVariableRejectsInvalidSizeOrFormat(int length, string contentType, string expectedError)
    {
        var image = new PluginImageContent(new byte[length], contentType);
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, image);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationContent: "&test.image"), false);

        var error = Assert.IsType<InvalidOperationException>(Assert.Single(fixture.Logger.Exceptions));
        Assert.Equal(expectedError, error.Message);
        Assert.False(notifications.Sent.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationFailedResultsFailActionAndRemoveTemporaryImage(bool partialFailure)
    {
        var notifications = new RecordingNotifications(_ => Task.FromResult<IReadOnlyList<NotificationChannelResult>>(partialFailure
            ? [new("Windows", true), new("QQBot", false), new("qqbot", false)]
            : []));
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([137, 80, 78, 71], "image/png"));

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationContent: "&test.image"), false);

        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Fact]
    public async Task NotificationDeliveryExceptionFailsActionAndRemovesTemporaryImage()
    {
        var notifications = new RecordingNotifications(_ => Task.FromException<IReadOnlyList<NotificationChannelResult>>(new IOException("测试投递异常")));
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([137, 80, 78, 71], "image/png"));

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationContent: "&test.image"), false);

        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationCancellationRemovesTemporaryImageAndSkipsRemainingActions(bool duringDelivery)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new RecordingNotifications(async token =>
        {
            if (duringDelivery)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            else
                await release.Task;
            return [new("Test", true)];
        });
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([137, 80, 78, 71], "image/png"));
        StartNotification(fixture, new(TaskActionType.SendNotification, NotificationContent: "&test.image"));
        await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await CancelNotificationAsync(fixture, notifications, () => release.TrySetResult());

        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task NotificationLocalImagePathRemainsUnchangedAfterDelivery(string outcome)
    {
        var notifications = new RecordingNotifications(async token =>
        {
            if (outcome == "cancel")
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [new("Test", outcome != "failure")];
        });
        await using var fixture = new TaskServiceFixture(notifications);
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "通知图片.png");
        var image = new byte[] { 137, 80, 78, 71 };
        await File.WriteAllBytesAsync(path, image);
        var action = new TaskAction(TaskActionType.SendNotification, NotificationContent: $"&image=\"{path}\"");
        if (outcome == "cancel")
        {
            StartNotification(fixture, action);
            await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await CancelNotificationAsync(fixture, notifications);
        }
        else
        {
            await RunNotificationAsync(fixture, action, outcome == "success");
        }

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(path, sent.Message.ImagePath);
        Assert.Equal("", sent.Message.Content);
        Assert.Equal(image, notifications.ImageBytes);
        Assert.True(File.Exists(path));
        Assert.Equal(image, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(" \t ", false)]
    [InlineData(" QQBot ", true)]
    public async Task NotificationChannelWhitespaceIsPassedWithoutNormalization(string channel, bool ignoreEnabled)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification,
            NotificationContent: "测试通知", NotificationChannel: channel), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(channel, sent.Channel);
        Assert.Equal(ignoreEnabled, sent.IgnoreEnabled);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("任务标题", "任务标题")]
    public async Task NotificationUsesConfiguredTaskTitle(string? title, string expectedTitle)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification,
            NotificationTitle: title, NotificationContent: "测试通知"), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedTitle, sent.Message.Title);
    }

    [Fact]
    public async Task NotificationResolvesRequestedVariablesAndSendTimeButPreservesTitle()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        IReadOnlyList<string>? requested = null;
        var calls = 0;
        var image = new byte[] { 137, 80, 78, 71 };
        ((IPluginNotificationVariableApi)scope).Register([
            new("$test.summary", "结果"), new("&test.image", "图片", Type: PluginVariableType.Image)
        ], (names, _, _) =>
        {
            calls++;
            requested = names;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["$test.summary"] = "已完成", ["&test.image"] = new PluginImageContent(image, "image/png")
            });
        });
        var before = DateTimeOffset.Now.AddSeconds(-1);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification,
            NotificationTitle: "任务通知 $test.summary", NotificationContent: "结果：$test.summary\n\n&test.image\n$time\n$unknown"), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("任务通知 $test.summary", sent.Message.Title);
        var lines = sent.Message.Content.Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.Equal("结果：已完成", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("$unknown", lines[3]);
        Assert.True(DateTimeOffset.TryParseExact(lines[2], "[yyyy-MM-dd HH:mm:ss]", null,
            System.Globalization.DateTimeStyles.AssumeLocal, out var sendTime));
        Assert.InRange(sendTime, before, DateTimeOffset.Now.AddSeconds(1));
        Assert.Equal(1, calls);
        Assert.NotNull(requested);
        Assert.Equal(2, requested.Count);
        Assert.Contains("$test.summary", requested);
        Assert.Contains("&test.image", requested);
        Assert.Equal(image, notifications.ImageBytes);
        AssertTemporaryNotificationImageRemoved(notifications);
    }

    private static IDisposable RegisterNotificationImage(TaskServiceFixture fixture, PluginImageContent image)
    {
        var scope = fixture.Extensions.CreateScope("test", "测试插件");
        ((IPluginNotificationVariableApi)scope).Register([new("&test.image", "图片", Type: PluginVariableType.Image)],
            (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["&test.image"] = image }));
        return scope;
    }

    private static void StartNotification(TaskServiceFixture fixture, TaskAction action)
    {
        fixture.Tasks.Create(new("Notification", true, new(TaskTriggerType.WinPilotStartup),
            [action with { ContinueOnFailure = true }, new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
        fixture.Tasks.RunNow("Notification");
    }

    private static async Task RunNotificationAsync(TaskServiceFixture fixture, TaskAction action, bool succeeds)
    {
        StartNotification(fixture, action);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failures = fixture.Logger.Messages.Where(message => message.StartsWith("任务「Notification」第 1 个动作失败：", StringComparison.Ordinal));
        if (succeeds) Assert.Empty(failures);
        else Assert.Single(failures);
    }

    private static async Task CancelNotificationAsync(TaskServiceFixture fixture, RecordingNotifications notifications, Action? release = null)
    {
        fixture.Logger.ActionCancelled = true;
        var stopping = fixture.Tasks.StopAsync(CancellationToken.None);
        release?.Invoke();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Logger.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(notifications.Token.IsCancellationRequested);
        Assert.Contains("任务「Notification」已取消", fixture.Logger.Messages);
        Assert.Equal(0, fixture.Programs.Launches);
    }

    private static void AssertTemporaryNotificationImageRemoved(RecordingNotifications notifications)
    {
        Assert.True(notifications.Sent.Task.IsCompletedSuccessfully);
        var path = notifications.Sent.Task.Result.Message.ImagePath;
        Assert.NotNull(path);
        Assert.NotNull(notifications.ImageBytes);
        Assert.False(File.Exists(path));
    }
}
