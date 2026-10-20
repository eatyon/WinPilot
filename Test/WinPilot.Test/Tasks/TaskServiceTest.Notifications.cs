using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using WinPilot.Core;
using WinPilot.Core.Tasks;
using WinPilot.Core.Notifications;
using WinPilot.Features.Plugins;
using WinPilot.Features.Notifications;
using WinPilot.Features.Tasks;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    [Theory]
    [InlineData(null, "请选择发送渠道")]
    [InlineData("", "请选择发送渠道")]
    [InlineData(" ", "请选择发送渠道")]
    [InlineData("all-enabled", "通知渠道无效")]
    [InlineData("unknown", "通知渠道无效")]
    public Task NotificationSaveRejectsInvalidChannelWithoutChangingFileOrMemory(string? channel, string message) =>
        AssertNotificationSaveRejectedAsync(channel, "changed", "notificationChannel", message);

    [Theory]
    [InlineData(null, "请选择发送渠道")]
    [InlineData("", "请选择发送渠道")]
    [InlineData(" ", "请选择发送渠道")]
    [InlineData("all-enabled", "通知渠道无效")]
    [InlineData("unknown", "通知渠道无效")]
    public async Task NotificationPlansRejectInvalidResolvedChannelBeforeImageWork(string? channel, string message)
    {
        foreach (var returnedPlan in new[] { false, true })
        {
            var notifications = new RecordingNotifications();
            await using var fixture = new TaskServiceFixture(notifications);
            var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.SendNotification,
                new Dictionary<string, object?>
                {
                    ["channel"] = "$param.channel", ["content"] = "content", ["image"] = new PluginImageContent([], "invalid")
                }), new Dictionary<string, object?> { ["channel"] = channel }, returnedPlan: returnedPlan);
            Assert.Equal(message, Assert.IsType<InvalidOperationException>(error).Message);
            Assert.False(notifications.Sent.Task.IsCompleted);
        }
    }

    [Fact]
    public void TaskNotificationTime_IsResolvedAtSendTimeAndKeepsTriggerVariables()
    {
        var triggerTime = DateTimeOffset.Now.AddHours(-1);
        var context = new PluginTaskRunContext("test", "test-plugin", "log-trigger", triggerTime,
            new Dictionary<string, object?>
            {
                ["$time"] = $"[{triggerTime:yyyy-MM-dd HH:mm:ss}]",
                ["$test.logtime"] = "12:34:56"
            });

        var before = DateTimeOffset.Now.AddSeconds(-1);
        var result = TaskService.ResolveNotificationContent("$time\n$test.logtime", context).Split(Environment.NewLine);
        var after = DateTimeOffset.Now.AddSeconds(1);

        Assert.True(DateTimeOffset.TryParseExact(result[0], "[yyyy-MM-dd HH:mm:ss]", null,
            System.Globalization.DateTimeStyles.AssumeLocal, out var sendTime));
        Assert.InRange(sendTime, before, after);
        Assert.Equal("12:34:56", result[1]);
    }

    [Theory]
    [InlineData("&test.image&test.image", "图片仅允许一个")]
    [InlineData("&test.image\n&test.image", "图片仅允许一个")]
    [InlineData("&test.image1234566", "图片变量必须单独占一行")]
    public void ImageVariableValidationRejectsDuplicateAndMalformedReferences(string content, string expectedMessage)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test Plugin");
        ((IPluginNotificationVariableApi)scope).Register([
            new("&test.image", "Image", Type: PluginVariableType.Image)
        ]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TaskService.ValidateImageVariables(content.Replace("\\n", "\n"), new TaskVariableCatalog(registry)));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Theory]
    [InlineData("&image=\"C:\\a.png\"\n&image=\"C:\\b.png\"")]
    [InlineData("&image=\"C:\\a.png\"&image=\"C:\\b.png\"")]
    [InlineData("&test.image\n&image=\"C:\\a.png\"")]
    public void NotificationImageValidationAllowsOnlyOneImageSource(string content)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test Plugin");
        ((IPluginNotificationVariableApi)scope).Register([
            new("&test.image", "Image", Type: PluginVariableType.Image)
        ]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TaskService.ValidateNotificationImages(content, new TaskVariableCatalog(registry)));

        Assert.Equal("图片仅允许一个", exception.Message);
    }

    [Fact]
    public async Task NotificationImageVariableMaterializesToTemporaryFile()
    {
        var image = new PluginImageContent([1, 2, 3, 4], "image/png");
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, image);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(sent.Message.ImagePath);
        Assert.Equal(image.Content, notifications.ImageBytes);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(sent.Message.ImagePath));
        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Fact]
    public void NotificationResultsRejectFailedChannels()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => NotificationDelivery.ValidateResults([
            new NotificationChannelResult("Windows", true),
            new NotificationChannelResult("QQBot", false),
            new NotificationChannelResult("Telegram", false)
        ]));

        Assert.Equal("通知发送失败：QQBot、Telegram", exception.Message);
    }

    [Fact]
    public void NotificationResultsRequireAtLeastOneChannel()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => NotificationDelivery.ValidateResults([]));

        Assert.Equal("没有可用的通知渠道", exception.Message);
    }

    [Fact]
    public async Task DirectImageDirectiveExtractsQuotedAbsolutePath()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        var path = Path.Combine(Path.GetTempPath(), $"winpilot-test-{Guid.NewGuid():N}.png");
        var bytes = new byte[] { 137, 80, 78, 71 };
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var (content, imagePath) = TaskService.ExtractDirectImagePath($"签到完成\n&image=\"{path}\"");
            Assert.Equal("签到完成", content);
            Assert.Equal(path, imagePath);
            await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: $"签到完成\n&image=\"{path}\""), true);
            var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(path, sent.Message.ImagePath);
            Assert.Equal("签到完成", sent.Message.Content);
            Assert.Equal(bytes, notifications.ImageBytes);
            Assert.True(File.Exists(path));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NotificationImageVariableMustBeUsedAlone()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        scope.Register([new("image", "图片触发")]);
        fixture.Tasks.Create(new("Notification", true, new(TaskTriggerType.Extension, PluginId: "test", TriggerId: "image"),
            [new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&image=\"前缀-&test.image\"", ContinueOnFailure: true),
                new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));

        scope.Emit(new("image", DateTimeOffset.Now, new Dictionary<string, object?> { ["&test.image"] = new PluginImageContent([1], "image/png") }));
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var exception = Assert.IsType<InvalidOperationException>(Assert.Single(fixture.Logger.Exceptions));
        Assert.Equal("图片变量必须单独填写，不能与其他文字组合", exception.Message);
        Assert.False(notifications.Sent.Task.IsCompleted);
    }

    [Fact]
    public async Task UnavailableImageVariableDoesNotFallBackToLiteralText()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&image=\"&test.image\""), false);

        var exception = Assert.IsType<InvalidOperationException>(Assert.Single(fixture.Logger.Exceptions));
        Assert.Equal("图片变量 &test.image 不可用", exception.Message);
        Assert.False(notifications.Sent.Task.IsCompleted);
    }

    private static async Task AssertNotificationSaveRejectedAsync(string? channel, string content, string field, string message)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new("Existing", false, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.SendNotification, NotificationContent: "original", NotificationChannel: "QQBot")]));
        var path = Path.Combine(fixture.Paths.TasksDirectory, "Existing.json");
        var original = File.ReadAllText(path);
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        foreach (var create in new[] { true, false })
        {
            var request = new
            {
                name = create ? "Rejected" : "Existing", enabled = false,
                trigger = new { type = "WinPilotStartup" },
                actions = new[] { new { type = "SendNotification", notificationContent = content, notificationChannel = channel } }
            };
            using var response = create
                ? await client.PostAsJsonAsync("/api/tasks", request)
                : await client.PutAsJsonAsync("/api/tasks/Existing", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(message, json.RootElement.GetProperty("errors").GetProperty($"actions[0].{field}")[0].GetString());
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(File.Exists(Path.Combine(fixture.Paths.TasksDirectory, "Rejected.json")));
            var saved = Assert.Single(fixture.Tasks.GetAll());
            Assert.Equal("original", Assert.Single(saved.Actions).NotificationContent);
            Assert.Equal("QQBot", saved.Actions[0].NotificationChannel);
        }
    }

}
