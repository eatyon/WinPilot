using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using WinPilot.Core.Notifications;
using WinPilot.Core.Tasks;
using WinPilot.Features.Notifications;
using WinPilot.Features.Plugins;
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
                    ["channel"] = "$param.channel",
                    ["content"] = "content",
                    ["image"] = new PluginImageContent([], "invalid")
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

        Assert.True(DateTimeOffset.TryParseExact(
            result[0],
            "[yyyy-MM-dd HH:mm:ss]",
            null,
            System.Globalization.DateTimeStyles.AssumeLocal,
            out var sendTime));
        Assert.InRange(sendTime, before, after);
        Assert.Equal("12:34:56", result[1]);
    }

    [Theory]
    [InlineData("&test.image&test.image")]
    [InlineData("&test.image\n&test.image")]
    public void ImageVariableValidationRejectsDuplicateReferences(string content)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test Plugin");
        ((IPluginNotificationVariableApi)scope).Register([
            new("&test.image", "Image", Type: PluginVariableType.Image)
        ]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TaskService.ValidateImageVariables(content, new TaskVariableCatalog(registry)));

        Assert.Equal("图片仅允许一个", exception.Message);
    }

    [Theory]
    [InlineData("&image=\"C:\\a.png\"\n&image=\"C:\\b.png\"")]
    [InlineData("示例：&image=\"C:\\a.png\" 和 &image=\"C:\\b.png\"")]
    [InlineData("&image=\"C:\\a.png\"&image=\"C:\\b.png\"")]
    [InlineData("&test.image\n&image=\"C:\\a.png\"")]
    [InlineData("正文&test.image 和 &image=\"C:\\a.png\"")]
    [InlineData("正文&screenshot 和 &image=\"C:\\a.png\"")]
    [InlineData("正文&screenshot 和 &screenshot")]
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

    [Theory]
    [InlineData("截图&screenshot完成", "&screenshot")]
    [InlineData("图片&test.image完成", "&test.image")]
    public void ImageVariablesCanAppearInsideNotificationText(string content, string name)
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test Plugin");
        ((IPluginNotificationVariableApi)scope).Register([new("&test.image", "Image", Type: PluginVariableType.Image)]);

        Assert.Equal(
            name,
            Assert.Single(TaskService.ValidateImageVariables(content, new TaskVariableCatalog(registry))));
        Assert.Equal(content.Replace(name, string.Empty), VariableResolver.Remove(content, name));
    }

    [Theory]
    [InlineData("&test.image", "")]
    [InlineData("图片&test.image正文", "图片正文")]
    [InlineData("&test.image 正文", " 正文")]
    [InlineData("正文 &test.image", "正文 ")]
    [InlineData("正文\n&test.image", "正文\n")]
    [InlineData("&test.image123 &test.image", "&test.image123 ")]
    public async Task NotificationImageVariableMaterializesToTemporaryFile(string content, string expected)
    {
        var image = new PluginImageContent([1, 2, 3, 4], "image/png");
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, image);

        await RunNotificationAsync(
            fixture,
            new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: content),
            true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(sent.Message.ImagePath);
        Assert.Equal(image.Content, notifications.ImageBytes);
        Assert.Equal(expected.Replace("\n", Environment.NewLine), sent.Message.Content);
        Assert.Equal(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(sent.Message.ImagePath));
        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Fact]
    public void NotificationResultsRejectFailedChannels()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            NotificationDelivery.ValidateResults([
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

    [Theory]
    [InlineData("&image=C:\\image.png")]
    [InlineData("&image=")]
    [InlineData("&image=\"C:\\image.png")]
    [InlineData("&image=C:\\image.png\"")]
    [InlineData("&image='C:\\image.png'")]
    [InlineData("&image=C:\\a.png\n&image=C:\\b.png")]
    [InlineData("&image=\"C:\\image.png\n下一行\"")]
    [InlineData("正文&unknown.image结尾")]
    [InlineData("正文&screenshot123结尾")]
    public async Task UnrecognizedImageReferencesAreSentAsText(string content)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        await RunNotificationAsync(
            fixture,
            new(TaskActionType.SendNotification,
            NotificationChannel: "QQBot", NotificationContent: content),
            true);
        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(content.Replace("\n", Environment.NewLine), sent.Message.Content);
        Assert.Null(sent.Message.ImagePath);
        Assert.Null(notifications.ImageBytes);
    }

    [Theory]
    [InlineData("&image=\"\"", "图片路径不能为空")]
    [InlineData("&image=\"   \"", "图片路径不能为空")]
    [InlineData("正文&image=\"\"结尾", "图片路径不能为空")]
    [InlineData("&image=\"image.png\"", "图片路径必须是绝对路径")]
    [InlineData("&image=\"C:image.png\"", "图片路径必须是绝对路径")]
    [InlineData("&image=\"C:\\image.gif\"", "通知图片仅支持 PNG 或 JPEG")]
    [InlineData("正文&image=\"C:\\image\"结尾", "通知图片仅支持 PNG 或 JPEG")]
    public Task InvalidLocalImagePathIsRejectedWithoutChangingFileOrMemory(string content, string message) =>
        AssertNotificationSaveRejectedAsync("QQBot", content, "notificationContent", message);

    [Theory]
    [InlineData("image.png", "图片路径必须是绝对路径")]
    [InlineData("C:\\image.gif", "通知图片仅支持 PNG 或 JPEG")]
    public async Task DirectImageDirectiveRevalidatesPathFormatBeforeSending(string path, string message)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.SendNotification,
            new Dictionary<string, object?> { ["channel"] = "QQBot", ["content"] = $"&image=\"{path}\"" }));
        Assert.Equal(message, Assert.IsType<InvalidOperationException>(error).Message);
        Assert.False(notifications.Sent.Task.IsCompleted);
    }

    [Theory]
    [InlineData(".png", false)]
    [InlineData(".jpg", false)]
    [InlineData(".JPEG", false)]
    [InlineData(".png", true)]
    public async Task LocalImageSaveDoesNotRequireAnExistingOrSmallFile(string extension, bool oversized)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "image" + extension);
        if (oversized)
        {
            using var file = File.Create(path);
            file.SetLength(NotificationDelivery.MaxImageSizeBytes + 1L);
        }
        var content = $"正文&image=\"{path}\"";
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        var request = new
        {
            name = "Image",
            enabled = false,
            trigger = new { type = "WinPilotStartup" },
            actions = new[] { new {
                type = "SendNotification",
                notificationChannel = "QQBot",
                notificationContent = content
} }
        };
        using var created = await client.PostAsJsonAsync("/api/tasks", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var updated = await client.PutAsJsonAsync("/api/tasks/Image", request);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(content, Assert.Single(Assert.Single(fixture.Tasks.GetAll()).Actions).NotificationContent);
        Assert.False(notifications.Sent.Task.IsCompleted);

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.SendNotification,
            new Dictionary<string, object?> { ["channel"] = "QQBot", ["content"] = content }));
        if (oversized)
        {
            Assert.Equal("通知图片不能超过 10 MB", Assert.IsType<InvalidOperationException>(error).Message);
        }
        else
        {
            Assert.IsType<FileNotFoundException>(error);
        }
        Assert.False(notifications.Sent.Task.IsCompleted);
    }

    [Theory]
    [InlineData("签到完成\n{0}", "签到完成\n")]
    [InlineData("签到完成{0}", "签到完成")]
    [InlineData("{0}签到完成", "签到完成")]
    [InlineData("签到{0}完成", "签到完成")]
    public async Task DirectImageDirectiveExtractsQuotedAbsolutePath(string template, string expected)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        var path = Path.Combine(Path.GetTempPath(), $"winpilot-test-{Guid.NewGuid():N}.png");
        var bytes = new byte[] {
            137,
            80,
            78,
            71
};
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var input = string.Format(template, $"&image=\"{path}\"");
            var (content, imagePath) = TaskService.ExtractDirectImagePath(input);
            Assert.Equal(expected, content);
            Assert.Equal(path, imagePath);
            await RunNotificationAsync(
                fixture,
                new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: input),
                true);
            var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(path, sent.Message.ImagePath);
            Assert.Equal(expected.Replace("\n", Environment.NewLine), sent.Message.Content);
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
    public async Task LocalImagePathDoesNotInterpretEmbeddedImageVariables()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "prefix-&test.image image.png");
        var bytes = new byte[] {
            1,
            2,
            3
};
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        await File.WriteAllBytesAsync(path, bytes);
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        scope.Register([new("image", "图片触发")]);
        fixture.Tasks.Create(new(
            "Notification",
            true,
            new(TaskTriggerType.Extension, PluginId: "test", TriggerId: "image"),
            [
                new(
                    TaskActionType.SendNotification,
                    NotificationChannel: "QQBot",
                    NotificationContent: $"&image=\"{path}\"",
                    ContinueOnFailure: true),
                new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)
]));

        scope.Emit(new("image", DateTimeOffset.Now, new Dictionary<string, object?> { ["&test.image"] = new PluginImageContent([1], "image/png") }));
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(fixture.Logger.Exceptions);
        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(path, sent.Message.ImagePath);
        Assert.Equal(bytes, notifications.ImageBytes);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task LocalImagePathStaysLiteralWhileNotificationTextResolvesVariables()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "literal-$test.path image.png");
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        ((IPluginNotificationVariableApi)scope).Register([new("$test.path", "内容")],
            (_, _, _) =>
                Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["$test.path"] = "resolved" }));

        await RunNotificationAsync(
            fixture,
            new(
                TaskActionType.SendNotification,
                NotificationChannel: "QQBot",
                NotificationContent: $"$test.path\n&image=\"{path}\""),
            true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("resolved" + Environment.NewLine, sent.Message.Content);
        Assert.Equal(path, sent.Message.ImagePath);
        Assert.Equal(new byte[] {
            1,
            2,
            3
}, notifications.ImageBytes);
    }

    [Fact]
    public async Task UnavailableImageVariableDoesNotFallBackToLiteralText()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        ((IPluginNotificationVariableApi)scope).Register([new("&test.image", "图片", Type: PluginVariableType.Image)]);

        await RunNotificationAsync(
            fixture,
            new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"),
            false);

        var exception = Assert.IsType<InvalidOperationException>(Assert.Single(fixture.Logger.Exceptions));
        Assert.Equal("图片变量 &test.image 不可用", exception.Message);
        Assert.False(notifications.Sent.Task.IsCompleted);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("QQBot")]
    public async Task NotificationActionUsesSelectedChannelAndPersistsIt(string? channel)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        fixture.Tasks.Create(new(
            "Notification",
            true,
            new(TaskTriggerType.WinPilotStartup),
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
    }


    [Theory]
    [InlineData("测试通知", true)]
    [InlineData("", false)]
    public async Task NotificationEmptyImageVariableIsTreatedAsNoImage(string content, bool succeeds)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([], "image/png"));

        await RunNotificationAsync(
            fixture,
            new(
                TaskActionType.SendNotification,
                NotificationChannel: "QQBot",
                NotificationContent: string.IsNullOrEmpty(content) ? "&test.image" : $"{content}\n&test.image"),
            succeeds);

        if (succeeds)
        {
            var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(content + Environment.NewLine, sent.Message.Content);
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
    public async Task NotificationImageVariableAcceptsSizeBoundariesAndDeclaredFormat(
        int length,
        string contentType,
        string extension)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        var image = new byte[length];
        new Random(42).NextBytes(image);
        using var scope = RegisterNotificationImage(fixture, new(image, contentType));

        await RunNotificationAsync(
            fixture,
            new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"),
            true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("", sent.Message.Content);
        Assert.Equal(extension, Path.GetExtension(sent.Message.ImagePath));
        Assert.True(image.AsSpan().SequenceEqual(notifications.ImageBytes), "发送的图片内容与原始数据不一致");
        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Theory]
    [InlineData(10 * 1024 * 1024 + 1, "image/png", "通知图片不能超过 10 MB")]
    [InlineData(1, "image/gif", "通知图片仅支持 PNG 或 JPEG")]
    [InlineData(1, "image/webp", "通知图片仅支持 PNG 或 JPEG")]
    [InlineData(1, "image/jpg", "通知图片仅支持 PNG 或 JPEG")]
    public async Task NotificationImageVariableRejectsInvalidSizeOrFormat(
        int length,
        string contentType,
        string expectedError)
    {
        var image = new PluginImageContent(new byte[length], contentType);
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, image);

        await RunNotificationAsync(
            fixture,
            new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"),
            false);

        var error = Assert.IsType<InvalidOperationException>(Assert.Single(fixture.Logger.Exceptions));
        Assert.Equal(expectedError, error.Message);
        Assert.False(notifications.Sent.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationFailedResultsFailActionAndRemoveTemporaryImage(bool partialFailure)
    {
        var notifications = new RecordingNotifications(_ =>
            Task.FromResult<IReadOnlyList<NotificationChannelResult>>(partialFailure
            ? [new("Windows", true), new("QQBot", false), new("qqbot", false)]
            : []));
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([137, 80, 78, 71], "image/png"));

        await RunNotificationAsync(
            fixture,
            new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"),
            false);

        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Fact]
    public async Task NotificationDeliveryExceptionFailsActionAndRemovesTemporaryImage()
    {
        var notifications = new RecordingNotifications(_ => Task.FromException<IReadOnlyList<NotificationChannelResult>>(new IOException("测试投递异常")));
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([137, 80, 78, 71], "image/png"));

        await RunNotificationAsync(
            fixture,
            new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"),
            false);

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
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else
            {
                await release.Task;
            }
            return [new("Test", true)];
        });
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([137, 80, 78, 71], "image/png"));
        StartNotification(
            fixture,
            new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"));
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
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return [new("Test", outcome != "failure")];
        });
        await using var fixture = new TaskServiceFixture(notifications);
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "通知图片.png");
        var image = new byte[] {
            137,
            80,
            78,
            71
};
        await File.WriteAllBytesAsync(path, image);
        var action = new TaskAction(
            TaskActionType.SendNotification,
            NotificationChannel: "QQBot",
            NotificationContent: $"&image=\"{path}\"");
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
    [InlineData(" telegram ")]
    [InlineData(" QQBot ")]
    public async Task NotificationChannelWhitespaceIsPassedWithoutNormalization(string channel)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);

        await RunNotificationAsync(
            fixture,
            new(TaskActionType.SendNotification,
            NotificationContent: "测试通知", NotificationChannel: channel),
            true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(channel, sent.Channel);
    }

    [Fact]
    public async Task NotificationClearsOnlyRegisteredTextVariablesWithoutValues()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        var variables = (IPluginNotificationVariableApi)scope;
        variables.Register([new("$test.context", "触发上下文")]);
        variables.Register([
            new("$test.null", "空值"), new("$test.missing", "未返回"),
            new("$test.empty", "空字符串"), new("$test.number", "数字"), new("$test.flag", "布尔值")
        ], (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["$test.null"] = null,
            ["$test.empty"] = "",
            ["$test.number"] = 0,
            ["$test.flag"] = false
        }));
        using (var stopped = fixture.Extensions.CreateScope("stopped", "已停止插件"))
        {
            ((IPluginNotificationVariableApi)stopped).Register([new("$stopped.value", "已失效")]);
        }

        await RunNotificationAsync(fixture, new(
            TaskActionType.SendNotification,
            NotificationChannel: "QQBot",
            NotificationContent: "[$test.context][$test.null][$test.missing][$test.empty][$test.number][$test.flag][$unknown][$stopped.value]"), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("[][][][][0][False][$unknown][$stopped.value]", sent.Message.Content);
    }

    [Fact]
    public async Task NotificationResolvesRequestedVariablesAndSendTime()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        IReadOnlyList<string>? requested = null;
        var calls = 0;
        var image = new byte[] {
            137,
            80,
            78,
            71
};
        ((IPluginNotificationVariableApi)scope).Register([
            new("$test.summary", "结果"), new("&test.image", "图片", Type: PluginVariableType.Image)
        ], (names, _, _) =>
        {
            calls++;
            requested = names;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["$test.summary"] = "已完成",
                ["&test.image"] = new PluginImageContent(image, "image/png")
            });
        });
        var before = DateTimeOffset.Now.AddSeconds(-1);

        await RunNotificationAsync(
            fixture,
            new(
                TaskActionType.SendNotification,
                NotificationChannel: "QQBot",
                NotificationContent: "结果：$test.summary\n\n&test.image\n$time\n$unknown"),
            true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var lines = sent.Message.Content.Split(Environment.NewLine);
        Assert.Equal(5, lines.Length);
        Assert.Equal("结果：已完成", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("", lines[2]);
        Assert.Equal("$unknown", lines[4]);
        Assert.True(DateTimeOffset.TryParseExact(
            lines[3],
            "[yyyy-MM-dd HH:mm:ss]",
            null,
            System.Globalization.DateTimeStyles.AssumeLocal,
            out var sendTime));
        Assert.InRange(sendTime, before, DateTimeOffset.Now.AddSeconds(1));
        Assert.Equal(1, calls);
        Assert.NotNull(requested);
        Assert.Equal(2, requested.Count);
        Assert.Contains("$test.summary", requested);
        Assert.Contains("&test.image", requested);
        Assert.Equal(image, notifications.ImageBytes);
        AssertTemporaryNotificationImageRemoved(notifications);
    }


    private static async Task AssertNotificationSaveRejectedAsync(
        string? channel,
        string content,
        string field,
        string message)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new(
            "Existing",
            false,
            new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.SendNotification, NotificationContent: "original", NotificationChannel: "QQBot")]));
        var path = Path.Combine(fixture.Paths.TasksDirectory, "Existing.json");
        var original = File.ReadAllText(path);
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        foreach (var create in new[] { true, false })
        {
            var request = new
            {
                name = create ? "Rejected" : "Existing",
                enabled = false,
                trigger = new { type = "WinPilotStartup" },
                actions = new[] { new {
                    type = "SendNotification",
                    notificationContent = content,
                    notificationChannel = channel
} }
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

    private static IDisposable RegisterNotificationImage(TaskServiceFixture fixture, PluginImageContent image)
    {
        var scope = fixture.Extensions.CreateScope("test", "测试插件");
        ((IPluginNotificationVariableApi)scope).Register([new("&test.image", "图片", Type: PluginVariableType.Image)],
            (_, _, _) =>
                Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["&test.image"] = image }));
        return scope;
    }

    private static void StartNotification(TaskServiceFixture fixture, TaskAction action)
    {
        fixture.Tasks.Create(new("Notification", true, new(TaskTriggerType.WinPilotStartup),
            [
                action with { ContinueOnFailure = true },
                new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)
]));
        fixture.Tasks.RunNow("Notification");
    }

    private static async Task RunNotificationAsync(TaskServiceFixture fixture, TaskAction action, bool succeeds)
    {
        StartNotification(fixture, action);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failures = fixture.Logger.Messages.Where(message => message.StartsWith("任务「Notification」第 1 个动作失败：", StringComparison.Ordinal));
        if (succeeds)
        {
            Assert.Empty(failures);
        }
        else
        {
            Assert.Single(failures);
        }
    }

    private static async Task CancelNotificationAsync(
        TaskServiceFixture fixture,
        RecordingNotifications notifications,
        Action? release = null)
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

    private sealed class RecordingNotifications(Func<CancellationToken, Task<IReadOnlyList<NotificationChannelResult>>>? send = null) : INotificationService
    {
        internal TaskCompletionSource<(NotificationMessage Message, string? Channel)> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? ImageBytes { get; private set; }
        internal CancellationToken Token { get; private set; }

        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(
            NotificationMessage message,
            string? channel,
            CancellationToken cancellationToken = default)
        {
            ImageBytes = message.ImagePath is null ? null : File.ReadAllBytes(message.ImagePath);
            Token = cancellationToken;
            Sent.TrySetResult((message, channel));
            return send?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new("Test", true)]);
        }
    }
}
