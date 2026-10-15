using WinPilot.Core;
using WinPilot.Core.Input;
using WinPilot.Core.Programs;
using WinPilot.Features.Desktop;
using WinPilot.Features.Plugins;
using WinPilot.Features.Terminal;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.PluginContract;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;
using System.Text.Json;

namespace WinPilot.Test;

[Collection(TerminalTestCollection.Name)]
public sealed class PluginHostActionApiTest
{
    [Theory]
    [InlineData(PluginHostActionType.StartProgram, "启动程序", false)]
    [InlineData(PluginHostActionType.Delay, "延迟", true)]
    [InlineData(PluginHostActionType.Keyboard, "发送按键", true)]
    [InlineData(PluginHostActionType.SendNotification, "发送通知", true)]
    public async Task OperationLogsUseChineseNames(PluginHostActionType type, string name, bool succeeds)
    {
        var logger = new CommandBotTestObserver<PluginHostActionApi>();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(new RecordingProgramManager(), new KeyboardInputService(new NoOpKeyboardInputSimulator()),
            terminals, new RecordingNotificationService(), logger);

        var result = await api.ExecuteAsync(new(type, new Dictionary<string, object?> { ["content"] = "测试", ["keys"] = new[] { "F11" } }));

        Assert.Equal(succeeds, result.Succeeded);
        Assert.Collection(logger.Entries,
            entry =>
            {
                Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, entry.Level);
                Assert.Equal($"开始执行插件请求的主程序操作，操作类型：{name}", entry.Message);
            },
            entry =>
            {
                Assert.Equal(succeeds ? Microsoft.Extensions.Logging.LogLevel.Debug : Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
                Assert.Equal($"{(succeeds ? "插件请求的主程序操作已完成" : "执行插件请求的主程序操作失败")}，操作类型：{name}", entry.Message);
            });
    }

    [Fact]
    public async Task StartProgram_HonorsElevationAndSilentParameters()
    {
        var programs = new RecordingProgramManager();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(programs, new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals, new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);

        var result = await api.ExecuteAsync(new(PluginHostActionType.StartProgram, new Dictionary<string, object?>
        {
            ["programPath"] = "TestProgram.exe",
            ["arguments"] = "--test",
            ["workingDirectory"] = "C:\\TestProgram",
            ["runElevated"] = true,
            ["silent"] = false
        }));

        Assert.True(result.Succeeded);
        Assert.NotNull(programs.LastLaunch);
        Assert.True(programs.LastLaunch.RunElevated);
        Assert.False(programs.LastLaunch.Silent);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("qqbot", true)]
    public async Task SendNotification_RoutesThroughNotificationService(string? channel, bool ignoreEnabled)
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(new RecordingProgramManager(), new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals, notifications, NullLogger<PluginHostActionApi>.Instance);

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["content"] = "签到完成", ["channel"] = channel
        }));

        Assert.True(result.Succeeded);
        Assert.Equal("签到完成", notifications.Message?.Content);
        Assert.Equal("", notifications.Message?.Title);
        Assert.Equal(channel, notifications.Channel);
        Assert.Equal(ignoreEnabled, notifications.IgnoreEnabled);
    }

    [Fact]
    public async Task SendNotification_RejectsEmptyMessage()
    {
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(new RecordingProgramManager(), new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals, new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>()));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SendNotification_WritesImageForDeliveryAndRemovesTemporaryFile()
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(new RecordingProgramManager(), new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals, notifications, NullLogger<PluginHostActionApi>.Instance);
        var image = new byte[] { 137, 80, 78, 71 };

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["image"] = new PluginImageContent(image, "image/png")
        }));

        Assert.True(result.Succeeded);
        Assert.Equal(image, notifications.ImageBytes);
        Assert.NotNull(notifications.Message?.ImagePath);
        Assert.False(File.Exists(notifications.Message.ImagePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10 * 1024 * 1024 + 1)]
    public async Task SendNotification_RejectsInvalidImageSizeEvenWithText(int length)
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["content"] = "测试通知", ["image"] = new PluginImageContent(new byte[length], "image/png")
        }));

        Assert.False(result.Succeeded);
        Assert.Equal("通知图片大小必须在 1 字节到 10 MB 之间", result.Error);
        Assert.Null(notifications.Message);
    }

    [Theory]
    [InlineData(1, "image/png", ".png")]
    [InlineData(10 * 1024 * 1024, "image/png", ".png")]
    [InlineData(1, "IMAGE/JPEG", ".jpg")]
    [InlineData(10 * 1024 * 1024, "image/jpeg", ".jpg")]
    public async Task SendNotification_AcceptsImageSizeBoundariesAndDeclaredFormat(int length, string contentType, string extension)
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);
        var image = new byte[length];

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["image"] = new PluginImageContent(image, contentType)
        }));

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.NotNull(notifications.Message);
        Assert.Equal("", notifications.Message.Title);
        Assert.Equal("", notifications.Message.Content);
        Assert.Equal(extension, Path.GetExtension(notifications.Message.ImagePath));
        Assert.Equal(image, notifications.ImageBytes);
        AssertTemporaryImageRemoved(notifications);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("image/jpg")]
    public async Task SendNotification_RejectsUnsupportedImageFormat(string contentType)
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["image"] = new PluginImageContent([1], contentType)
        }));

        Assert.False(result.Succeeded);
        Assert.Equal("通知图片仅支持 PNG 或 JPEG", result.Error);
        Assert.Null(notifications.Message);
    }

    [Fact]
    public async Task SendNotification_NoAvailableChannelFailsAndRemovesTemporaryImage()
    {
        var notifications = new RecordingNotificationService(_ => Task.FromResult<IReadOnlyList<NotificationChannelResult>>([]));
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);

        var result = await api.ExecuteAsync(ImageNotification());

        Assert.False(result.Succeeded);
        Assert.Equal("没有可用的通知渠道", result.Error);
        AssertTemporaryImageRemoved(notifications);
    }

    [Fact]
    public async Task SendNotification_PartialFailureReportsOnlyFailedChannelsAndRemovesTemporaryImage()
    {
        var notifications = new RecordingNotificationService(_ => Task.FromResult<IReadOnlyList<NotificationChannelResult>>([
            new("Windows", true), new("QQBot", false), new("Telegram", false)
        ]));
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);

        var result = await api.ExecuteAsync(ImageNotification());

        Assert.False(result.Succeeded);
        Assert.Equal("通知发送失败：QQBot、Telegram", result.Error);
        AssertTemporaryImageRemoved(notifications);
    }

    [Fact]
    public async Task SendNotification_DeduplicatesFailedChannelsIgnoringCase()
    {
        var notifications = new RecordingNotificationService(_ => Task.FromResult<IReadOnlyList<NotificationChannelResult>>([
            new("Windows", true), new("QQBot", false), new("qqbot", false), new("Telegram", false), new("TELEGRAM", false)
        ]));
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);

        var result = await api.ExecuteAsync(ImageNotification());

        Assert.False(result.Succeeded);
        Assert.Equal("通知发送失败：QQBot、Telegram", result.Error);
        AssertTemporaryImageRemoved(notifications);
    }

    [Fact]
    public async Task SendNotification_DeliveryExceptionBecomesFailureAndRemovesTemporaryImage()
    {
        var notifications = new RecordingNotificationService(_ => Task.FromException<IReadOnlyList<NotificationChannelResult>>(new IOException("测试投递异常")));
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);

        var result = await api.ExecuteAsync(ImageNotification());

        Assert.False(result.Succeeded);
        Assert.Equal("测试投递异常", result.Error);
        AssertTemporaryImageRemoved(notifications);
    }

    [Fact]
    public async Task SendNotification_CancellationDuringDeliveryPropagatesAndRemovesTemporaryImage()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new RecordingNotificationService(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        });
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);
        var sending = api.ExecuteAsync(ImageNotification(), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            AssertTemporaryImageRemoved(notifications);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task SendNotification_CancellationAfterSuccessfulDeliveryRemovesTemporaryImage()
    {
        using var cancellation = new CancellationTokenSource();
        var notifications = new RecordingNotificationService(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new("Test", true)]);
        });
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.ExecuteAsync(ImageNotification(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        AssertTemporaryImageRemoved(notifications);
    }

    [Theory]
    [InlineData("", null, false)]
    [InlineData(" \t ", null, false)]
    [InlineData(" QQBot ", "QQBot", true)]
    public async Task SendNotification_NormalizesChannelWhitespace(string channel, string? expectedChannel, bool ignoreEnabled)
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["content"] = "测试通知", ["channel"] = channel
        }));

        Assert.True(result.Succeeded);
        Assert.Equal(expectedChannel, notifications.Channel);
        Assert.Equal(ignoreEnabled, notifications.IgnoreEnabled);
    }

    [Fact]
    public async Task SendNotification_PreservesLiteralContentAndEmptyTitle()
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);
        const string content = "$time\n&test.image\n&image=C:\\test.png";

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["content"] = content
        }));

        Assert.True(result.Succeeded);
        Assert.NotNull(notifications.Message);
        Assert.Equal("", notifications.Message.Title);
        Assert.Equal(content, notifications.Message.Content);
        Assert.Null(notifications.Message.ImagePath);
    }

    [Fact]
    public async Task SendNotification_LocalImageContentKeepsSourceFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"WinPilot-local-image-test-{Guid.NewGuid():N}.png");
        var image = new byte[] { 137, 80, 78, 71 };
        try
        {
            await File.WriteAllBytesAsync(path, image);
            var notifications = new RecordingNotificationService();
            await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
            var api = CreateApi(terminals, notifications);

            var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
            {
                ["image"] = new PluginImageContent(await File.ReadAllBytesAsync(path), "image/png")
            }));

            Assert.True(result.Succeeded);
            Assert.Equal(image, notifications.ImageBytes);
            Assert.NotEqual(path, notifications.Message?.ImagePath);
            AssertTemporaryImageRemoved(notifications);
            Assert.True(File.Exists(path));
            Assert.Equal(image, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task HostActionStartProgramPreservesMissingAndNullDefaults(bool explicitNull, bool silent)
    {
        var programs = new RecordingProgramManager();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(programs, new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals,
            new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);
        var values = new Dictionary<string, object?> { ["programPath"] = "app.exe" };
        if (explicitNull)
            foreach (var key in new[] { "arguments", "workingDirectory", "runElevated", "silent" }) values[key] = null;

        var result = await api.ExecuteAsync(new(PluginHostActionType.StartProgram, values));

        Assert.True(result.Succeeded);
        Assert.Equal(new ProgramLaunchRequest("app.exe", "", "", false, silent), programs.LastLaunch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostActionStartProgramDistinguishesMissingPathFromNull(bool explicitNull)
    {
        var programs = new RecordingProgramManager();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(programs, new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals,
            new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);
        var values = new Dictionary<string, object?>();
        if (explicitNull) values["programPath"] = null;

        var result = await api.ExecuteAsync(new(PluginHostActionType.StartProgram, values));

        Assert.Equal(explicitNull, result.Succeeded);
        if (explicitNull) Assert.Equal("", programs.LastLaunch?.ProgramPath);
        else
        {
            Assert.Equal("缺少 programPath", result.Error);
            Assert.Null(programs.LastLaunch);
        }
    }

    [Theory]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("json-string")]
    public async Task HostActionStartProgramKeepsCurrentScalarConversions(string valueType)
    {
        var programs = new RecordingProgramManager();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(programs, new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals,
            new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);
        var values = new Dictionary<string, object?>
        {
            ["programPath"] = valueType == "json-string" ? HostActionJsonValue("\"123\"") : 123,
            ["arguments"] = 456,
            ["workingDirectory"] = 789,
            ["runElevated"] = valueType == "text" ? "True" : 2,
            ["silent"] = valueType == "text" ? "False" : 0
        };

        var result = await api.ExecuteAsync(new(PluginHostActionType.StartProgram, values));

        Assert.True(result.Succeeded);
        Assert.Equal(new ProgramLaunchRequest("123", "456", "789", true, false), programs.LastLaunch);
    }

    [Theory]
    [InlineData("runElevated", false)]
    [InlineData("runElevated", true)]
    [InlineData("silent", false)]
    [InlineData("silent", true)]
    public async Task HostActionBooleanConversionErrorsBecomeFailureResults(string key, bool json)
    {
        object value = json ? HostActionJsonValue("true") : "1";
        var expected = Record.Exception(() => Convert.ToBoolean(value));
        Assert.NotNull(expected);
        var programs = new RecordingProgramManager();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(programs, new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals,
            new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);

        var result = await api.ExecuteAsync(new(PluginHostActionType.StartProgram,
            new Dictionary<string, object?> { ["programPath"] = "app.exe", [key] = value }));

        Assert.False(result.Succeeded);
        Assert.Equal(expected.Message, result.Error);
        Assert.Null(programs.LastLaunch);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("negative")]
    [InlineData("text")]
    [InlineData("rounded")]
    [InlineData("boolean")]
    public async Task HostActionDelayKeepsZeroNegativeAndConvertibleValues(string valueType)
    {
        var values = new Dictionary<string, object?>();
        if (valueType != "missing") values["delaySeconds"] = valueType switch
        {
            "null" => null, "negative" => -1, "text" => "0", "rounded" => 0.4, _ => false
        };
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, new RecordingNotificationService());

        var result = await api.ExecuteAsync(new(PluginHostActionType.Delay, values)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("overflow")]
    [InlineData("json")]
    public async Task HostActionIntegerConversionErrorsBecomeFailureResults(string valueType)
    {
        object value = valueType switch { "text" => "1.5", "overflow" => long.MaxValue, _ => HostActionJsonValue("0") };
        var expected = Record.Exception(() => Convert.ToInt32(value));
        Assert.NotNull(expected);
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, new RecordingNotificationService());

        var result = await api.ExecuteAsync(new(PluginHostActionType.Delay,
            new Dictionary<string, object?> { ["delaySeconds"] = value }));

        Assert.False(result.Succeeded);
        Assert.Equal(expected.Message, result.Error);
    }

    [Fact]
    public async Task HostActionDelayDoesNotApplyNativeTaskUpperLimit()
    {
        using var cancellation = new CancellationTokenSource();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, new RecordingNotificationService());
        var pending = api.ExecuteAsync(new(PluginHostActionType.Delay,
            new Dictionary<string, object?> { ["delaySeconds"] = 86401 }), cancellation.Token);
        try
        {
            Assert.False(pending.IsCompleted);
        }
        finally { cancellation.Cancel(); }

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData("strings", true)]
    [InlineData("objects", false)]
    [InlineData("json", false)]
    [InlineData("text", false)]
    [InlineData("null", false)]
    [InlineData("missing", false)]
    public async Task HostActionKeyboardAcceptsOnlyStringEnumerables(string valueType, bool succeeds)
    {
        var keyboard = new RecordingHostKeyboard();
        var values = new Dictionary<string, object?>();
        if (valueType != "missing") values["keys"] = valueType switch
        {
            "strings" => new[] { "F11" }.Select(key => key),
            "objects" => new object[] { "F11" },
            "json" => HostActionJsonValue("[\"F11\"]"),
            "text" => "F11",
            _ => null
        };
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(new RecordingProgramManager(), new KeyboardInputService(keyboard), terminals,
            new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);

        var result = await api.ExecuteAsync(new(PluginHostActionType.Keyboard, values));

        Assert.Equal(succeeds, result.Succeeded);
        if (succeeds) Assert.Equal(new[] { KeyboardKey.F11 }, keyboard.Keys);
        else
        {
            Assert.Equal("每次只能发送 1 到 3 个按键", result.Error);
            Assert.Null(keyboard.Keys);
        }
    }

    [Fact]
    public async Task HostActionShellMissingTypeUsesCmd()
    {
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        await terminals.DisposeAsync();
        var api = CreateApi(terminals, new RecordingNotificationService());
        var expected = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            terminals.ExecuteTaskCommandAsync("cmd", "echo test", CancellationToken.None));
        var values = new Dictionary<string, object?> { ["command"] = "echo test" };

        var missing = await api.ExecuteAsync(new(PluginHostActionType.Shell, values));
        values["shell"] = "cmd";
        var explicitCmd = await api.ExecuteAsync(new(PluginHostActionType.Shell, values));
        values["shell"] = null;
        var explicitNull = await api.ExecuteAsync(new(PluginHostActionType.Shell, values));

        Assert.False(missing.Succeeded);
        Assert.False(explicitCmd.Succeeded);
        Assert.Equal(expected.Message, missing.Error);
        Assert.Equal(explicitCmd.Error, missing.Error);
        Assert.False(explicitNull.Succeeded);
        Assert.Equal("不支持的终端类型", explicitNull.Error);
        Assert.Equal(0, terminals.ActiveSessionCount);

        Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")));
        var previousComSpec = Environment.GetEnvironmentVariable("ComSpec");
        var missingExecutable = Path.Combine(Path.GetTempPath(), $"winpilot-missing-shell-{Guid.NewGuid():N}.exe");
        Assert.False(File.Exists(missingExecutable));
        try
        {
            Environment.SetEnvironmentVariable("ComSpec", missingExecutable);
            await using var availableTerminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
            var availableApi = CreateApi(availableTerminals, new RecordingNotificationService());
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            values.Remove("shell");

            var missingExecutableResult = await availableApi.ExecuteAsync(new(PluginHostActionType.Shell, values), cancellation.Token);
            values["shell"] = "cmd";
            var explicitCmdExecutableResult = await availableApi.ExecuteAsync(new(PluginHostActionType.Shell, values), cancellation.Token);

            Assert.False(missingExecutableResult.Succeeded);
            Assert.Equal("未找到终端程序", missingExecutableResult.Error);
            Assert.False(explicitCmdExecutableResult.Succeeded);
            Assert.Equal(missingExecutableResult.Error, explicitCmdExecutableResult.Error);
            Assert.Equal(0, availableTerminals.ActiveSessionCount);
        }
        finally { Environment.SetEnvironmentVariable("ComSpec", previousComSpec); }
    }

    [Theory]
    [InlineData("null-shell", "不支持的终端类型")]
    [InlineData("missing-command", "请输入 Shell 命令")]
    [InlineData("null-command", "请输入 Shell 命令")]
    public async Task HostActionShellPreservesNullAndMissingFieldErrors(string valueType, string expectedError)
    {
        var values = new Dictionary<string, object?> { ["shell"] = "cmd" };
        if (valueType == "null-shell")
        {
            values["shell"] = null;
            values["command"] = "echo test";
        }
        else if (valueType == "null-command") values["command"] = null;
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, new RecordingNotificationService());

        var result = await api.ExecuteAsync(new(PluginHostActionType.Shell, values));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedError, result.Error);
        Assert.Equal(0, terminals.ActiveSessionCount);
    }

    [Fact]
    public async Task HostActionNotificationDoesNotResolveParametersOrReadTitle()
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, notifications);

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["title"] = new UnreadableHostValue(), ["content"] = "$param.content", ["channel"] = " QQBot ", ["image"] = null
        }));

        Assert.True(result.Succeeded);
        Assert.Equal("", notifications.Message?.Title);
        Assert.Equal("$param.content", notifications.Message?.Content);
        Assert.Equal("QQBot", notifications.Channel);
        Assert.Null(notifications.Message?.ImagePath);
    }

    [Fact]
    public async Task HostActionStartProgramDoesNotResolveParameterReferences()
    {
        var programs = new RecordingProgramManager();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(programs, new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals,
            new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);

        var result = await api.ExecuteAsync(new(PluginHostActionType.StartProgram, new Dictionary<string, object?>
        {
            ["programPath"] = "$param.path", ["arguments"] = "$param.arguments"
        }));

        Assert.True(result.Succeeded);
        Assert.Equal(new ProgramLaunchRequest("$param.path", "$param.arguments", "", false, true), programs.LastLaunch);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task HostActionRejectsTypesOutsideContract(int type)
    {
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, new RecordingNotificationService());

        var result = await api.ExecuteAsync(new((PluginHostActionType)type, new Dictionary<string, object?>()));

        Assert.False(result.Succeeded);
        Assert.Equal("Host Action 类型不支持", result.Error);
    }

    [Fact]
    public async Task HostActionCancellationPrecedesParameterConversion()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = CreateApi(terminals, new RecordingNotificationService());

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.ExecuteAsync(
            new(PluginHostActionType.StartProgram, new Dictionary<string, object?>()), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static JsonElement HostActionJsonValue(string source)
    {
        using var document = JsonDocument.Parse(source);
        return document.RootElement.Clone();
    }

    private sealed class RecordingHostKeyboard : IKeyboardInputSimulator
    {
        internal IReadOnlyList<KeyboardKey>? Keys { get; private set; }
        public void Send(IReadOnlyList<KeyboardKey> keys) => Keys = keys.ToArray();
    }

    private sealed class UnreadableHostValue
    {
        public override string ToString() => throw new InvalidOperationException("不应读取此字段");
    }

    private static PluginHostActionApi CreateApi(TerminalSessionManager terminals, INotificationService notifications) =>
        new(new RecordingProgramManager(), new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals, notifications, NullLogger<PluginHostActionApi>.Instance);

    private static PluginHostActionStep ImageNotification() => new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
    {
        ["image"] = new PluginImageContent([137, 80, 78, 71], "image/png")
    });

    private static void AssertTemporaryImageRemoved(RecordingNotificationService notifications)
    {
        var path = notifications.Message?.ImagePath;
        Assert.NotNull(path);
        Assert.NotNull(notifications.ImageBytes);
        Assert.False(File.Exists(path));
    }

    private sealed class RecordingNotificationService(Func<CancellationToken, Task<IReadOnlyList<NotificationChannelResult>>>? send = null) : INotificationService
    {
        public NotificationMessage? Message { get; private set; }
        public string? Channel { get; private set; }
        public bool IgnoreEnabled { get; private set; }
        public byte[]? ImageBytes { get; private set; }
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default, string? channel = null, bool ignoreChannelEnabled = false)
        {
            Message = message;
            Channel = channel;
            IgnoreEnabled = ignoreChannelEnabled;
            ImageBytes = message.ImagePath is null ? null : File.ReadAllBytes(message.ImagePath);
            return send?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new("Test", true)]);
        }
    }

    private sealed class RecordingProgramManager : IProgramManager
    {
        public ProgramLaunchRequest? LastLaunch { get; private set; }
        public IReadOnlyList<InteractiveUser> GetInteractiveUsers() => [];
        public IReadOnlyList<RunningProgram> GetRunningPrograms(string userName) => [];
        public ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Close(IReadOnlyList<int> processIds, bool force) => throw new NotSupportedException();
        public void Launch(ProgramLaunchRequest request) => LastLaunch = request;
    }

    private sealed class NoOpKeyboardInputSimulator : IKeyboardInputSimulator
    {
        public void Send(IReadOnlyList<KeyboardKey> keys) { }
    }

}
