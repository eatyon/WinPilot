using System.Text.Json;
using WinPilot.Core.Input;
using WinPilot.Core.Programs;
using WinPilot.Core.Tasks;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostPlanStartProgramPreservesMissingAndNullDefaults(bool explicitNull)
    {
        await using var fixture = new TaskServiceFixture();
        var values = new Dictionary<string, object?> { ["programPath"] = "app.exe" };
        if (explicitNull)
            foreach (var key in new[] { "arguments", "workingDirectory", "runElevated", "silent" }) values[key] = null;

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.StartProgram, values));

        Assert.Null(error);
        Assert.Equal(new ProgramLaunchRequest("app.exe", "", "", false, false), Assert.Single(fixture.Programs.Requests));
    }

    [Fact]
    public async Task HostPlanStartProgramAcceptsAnExplicitNullPathAtConversion()
    {
        await using var fixture = new TaskServiceFixture();

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.StartProgram,
            new Dictionary<string, object?> { ["programPath"] = null }));

        Assert.Null(error);
        Assert.Equal("", Assert.Single(fixture.Programs.Requests).ProgramPath);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("json-string")]
    public async Task HostPlanStartProgramKeepsCurrentScalarConversions(string valueType)
    {
        await using var fixture = new TaskServiceFixture();
        var values = new Dictionary<string, object?>
        {
            ["programPath"] = valueType == "json-string" ? HostPlanJsonValue("\"123\"") : 123,
            ["arguments"] = 456,
            ["workingDirectory"] = 789,
            ["runElevated"] = valueType == "text" ? "True" : 2,
            ["silent"] = valueType == "text" ? "False" : 0
        };

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.StartProgram, values));

        Assert.Null(error);
        Assert.Equal(new ProgramLaunchRequest("123", "456", "789", true, false), Assert.Single(fixture.Programs.Requests));
    }

    [Theory]
    [InlineData("runElevated", false)]
    [InlineData("runElevated", true)]
    [InlineData("silent", false)]
    [InlineData("silent", true)]
    public async Task HostPlanBooleanConversionErrorsFailTheTaskAction(string key, bool json)
    {
        object value = json ? HostPlanJsonValue("true") : "1";
        var expected = Record.Exception(() => Convert.ToBoolean(value));
        Assert.NotNull(expected);
        await using var fixture = new TaskServiceFixture();

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.StartProgram,
            new Dictionary<string, object?> { ["programPath"] = "app.exe", [key] = value }));

        Assert.NotNull(error);
        Assert.Equal(expected.GetType(), error.GetType());
        Assert.Equal(expected.Message, error.Message);
        Assert.Empty(fixture.Programs.Requests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("negative")]
    [InlineData("text")]
    [InlineData("rounded")]
    [InlineData("boolean")]
    public async Task HostPlanDelayKeepsZeroNegativeAndConvertibleValues(string valueType)
    {
        await using var fixture = new TaskServiceFixture();
        var values = new Dictionary<string, object?>();
        if (valueType != "missing") values["delaySeconds"] = valueType switch
        {
            "null" => null, "negative" => -1, "text" => "0", "rounded" => 0.4, _ => false
        };

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.Delay, values));

        Assert.Null(error);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("overflow")]
    [InlineData("json")]
    public async Task HostPlanIntegerConversionErrorsFailTheTaskAction(string valueType)
    {
        object value = valueType switch { "text" => "1.5", "overflow" => long.MaxValue, _ => HostPlanJsonValue("0") };
        var expected = Record.Exception(() => Convert.ToInt32(value));
        Assert.NotNull(expected);
        await using var fixture = new TaskServiceFixture();

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.Delay,
            new Dictionary<string, object?> { ["delaySeconds"] = value }));

        Assert.NotNull(error);
        Assert.Equal(expected.GetType(), error.GetType());
        Assert.Equal(expected.Message, error.Message);
    }

    [Theory]
    [InlineData("strings", true)]
    [InlineData("objects", false)]
    [InlineData("json", false)]
    [InlineData("text", false)]
    [InlineData("null", false)]
    [InlineData("missing", false)]
    public async Task HostPlanKeyboardAcceptsOnlyStringEnumerables(string valueType, bool succeeds)
    {
        await using var fixture = new TaskServiceFixture();
        var values = new Dictionary<string, object?>();
        if (valueType != "missing") values["keys"] = valueType switch
        {
            "strings" => new[] { "F11" }.Select(key => key),
            "objects" => new object[] { "F11" },
            "json" => HostPlanJsonValue("[\"F11\"]"),
            "text" => "F11",
            _ => null
        };

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.Keyboard, values));

        if (succeeds)
        {
            Assert.Null(error);
            Assert.Equal(new[] { KeyboardKey.F11 }, fixture.SentKeys);
        }
        else
        {
            Assert.IsType<KeyboardInputException>(error);
            Assert.Equal("每次只能发送 1 到 3 个按键", error.Message);
            Assert.Null(fixture.SentKeys);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostPlanShellHasNoDefaultForMissingOrNullType(bool explicitNull)
    {
        await using var fixture = new TaskServiceFixture();
        var values = new Dictionary<string, object?> { ["command"] = "echo test" };
        if (explicitNull) values["shell"] = null;

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.Shell, values));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("不支持的终端类型", error.Message);
        Assert.Equal(0, fixture.Terminals.ActiveSessionCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    public async Task HostPlanShellKeepsCommandStringConversion(string valueType)
    {
        await using var fixture = new TaskServiceFixture();
        await fixture.Terminals.DisposeAsync();
        var values = new Dictionary<string, object?> { ["shell"] = "CMD" };
        if (valueType != "missing") values["command"] = valueType == "null" ? null : 123;

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.Shell, values));

        if (valueType == "number") Assert.IsType<ObjectDisposedException>(error);
        else
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal("请输入 Shell 命令", error.Message);
        }
        Assert.Equal(0, fixture.Terminals.ActiveSessionCount);
    }

    [Fact]
    public async Task HostPlanNotificationResolvesParametersAndContentButPreservesTitleAndChannel()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var variables = fixture.Extensions.CreateScope("vars", "Variables");
        ((IPluginNotificationVariableApi)variables).Register([new("$vars.result", "结果")], (_, _, _) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["$vars.result"] = "已完成" }));
        var parameters = new Dictionary<string, object?>
        {
            ["title"] = "$vars.result", ["content"] = "$vars.result\n$time", ["channel"] = " QQBot "
        };

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["title"] = "$param.title", ["content"] = "$param.content", ["channel"] = "$param.channel", ["image"] = null
        }), parameters);

        Assert.Null(error);
        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("$vars.result", sent.Message.Title);
        var lines = sent.Message.Content.Split(Environment.NewLine);
        Assert.Equal("已完成", lines[0]);
        Assert.True(DateTimeOffset.TryParseExact(lines[1], "[yyyy-MM-dd HH:mm:ss]", null,
            System.Globalization.DateTimeStyles.AssumeLocal, out _));
        Assert.Equal(" QQBot ", sent.Channel);
        Assert.True(sent.IgnoreEnabled);
        Assert.Null(sent.Message.ImagePath);
    }

    [Theory]
    [InlineData("path", "image 必须是 PluginImageContent")]
    [InlineData("bytes", "image 必须是 PluginImageContent")]
    [InlineData("empty", "通知图片大小必须在 1 字节到 10 MB 之间")]
    public async Task HostPlanNotificationKeepsExplicitImageTypeAndEmptyImageErrors(string valueType, string expectedError)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        object image = valueType switch
        {
            "path" => "C:\\image.png", "bytes" => new byte[] { 1 }, _ => new PluginImageContent([], "image/png")
        };

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.SendNotification,
            new Dictionary<string, object?> { ["content"] = "测试通知", ["image"] = image }));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(expectedError, error.Message);
        Assert.False(notifications.Sent.Task.IsCompleted);
    }

    [Theory]
    [InlineData("$param.target", "target", "resolved", "resolved")]
    [InlineData("$param.target", null, null, "$param.target")]
    [InlineData("$param.target", "TARGET", "resolved", "$param.target")]
    [InlineData("$Param.target", "target", "resolved", "$Param.target")]
    [InlineData("prefix $param.target", "target", "resolved", "prefix $param.target")]
    [InlineData("$param.target", "target", null, "")]
    [InlineData("$param.target", "target", 123, "123")]
    public async Task HostPlanParameterReferencesKeepExactLookupAndWholeValueResolution(string reference, string? key, object? value, string expectedArguments)
    {
        await using var fixture = new TaskServiceFixture();
        var parameters = new Dictionary<string, object?>();
        if (key is not null) parameters[key] = value;

        var error = await RunHostPlanAsync(fixture, new(PluginHostActionType.StartProgram,
            new Dictionary<string, object?> { ["programPath"] = "app.exe", ["arguments"] = reference }), parameters);

        Assert.Null(error);
        Assert.Equal(expectedArguments, Assert.Single(fixture.Programs.Requests).Arguments);
    }

    [Theory]
    [InlineData(PluginHostActionType.Delay, "delaySeconds", "0", PluginActionParameterType.Number)]
    [InlineData(PluginHostActionType.StartProgram, "runElevated", "true", PluginActionParameterType.Checkbox)]
    [InlineData(PluginHostActionType.Keyboard, "keys", "[\"F11\"]", PluginActionParameterType.MultiCheckbox)]
    public async Task HostPlanJsonParameterReferencesKeepCurrentUnsupportedTypes(PluginHostActionType type, string key, string json, PluginActionParameterType parameterType)
    {
        await using var fixture = new TaskServiceFixture();
        var values = new Dictionary<string, object?> { [key] = "$param.value" };
        if (type == PluginHostActionType.StartProgram) values["programPath"] = "app.exe";
        var definition = new PluginActionParameterDefinition("value", "Value", parameterType,
            Options: parameterType == PluginActionParameterType.MultiCheckbox ? [new("F11", "F11")] : null);

        var error = await RunHostPlanAsync(fixture, new(type, values),
            new Dictionary<string, object?> { ["value"] = HostPlanJsonValue(json) }, [definition]);

        if (type == PluginHostActionType.Keyboard)
        {
            Assert.IsType<KeyboardInputException>(error);
            Assert.Equal("每次只能发送 1 到 3 个按键", error.Message);
        }
        else Assert.IsType<InvalidCastException>(error);
        Assert.Empty(fixture.Programs.Requests);
        Assert.Null(fixture.SentKeys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostPlanValidationStillRequiresProgramPath(bool returnedPlan)
    {
        await using var fixture = new TaskServiceFixture();
        var step = new PluginHostActionStep(PluginHostActionType.StartProgram, new Dictionary<string, object?>());

        var error = returnedPlan
            ? await RunHostPlanAsync(fixture, step, returnedPlan: true)
            : await Assert.ThrowsAsync<ArgumentException>(() => RunHostPlanAsync(fixture, step));

        Assert.IsType<ArgumentException>(error);
        Assert.Equal("Host Action Plan 包含不支持的步骤", error.Message);
        Assert.Empty(fixture.Programs.Requests);
    }

    [Fact]
    public async Task HostPlanDelayDoesNotApplyNativeTaskUpperLimit()
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("host", "Host");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IPluginTaskActionApi)scope).Register([new("plan", "Plan")], async (_, _, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return PluginTaskActionResult.Success(new PluginHostActionPlan([
                new(PluginHostActionType.Delay, new Dictionary<string, object?> { ["delaySeconds"] = 86401 })]));
        });
        fixture.Tasks.Create(new("HostPlan", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Extension, PluginId: "host", ActionId: "plan")]));
        fixture.Tasks.RunNow("HostPlan");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = fixture.Tasks.StopAsync(CancellationToken.None);
            fixture.Logger.ActionCancelled = true;
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Logger.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("任务「HostPlan」已取消", fixture.Logger.Messages);
            Assert.Empty(fixture.Logger.Exceptions);
        }
        finally { release.TrySetResult(); }
    }

    private static async Task<Exception?> RunHostPlanAsync(TaskServiceFixture fixture, PluginHostActionStep step,
        IReadOnlyDictionary<string, object?>? parameters = null, IReadOnlyList<PluginActionParameterDefinition>? schema = null, bool returnedPlan = false)
    {
        using var scope = fixture.Extensions.CreateScope("host", "Host");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = new PluginHostActionPlan([step]);
        schema ??= parameters?.Keys.Select(key => new PluginActionParameterDefinition(key, key, PluginActionParameterType.Text)).ToArray();
        ((IPluginTaskActionApi)scope).Register([new("plan", "Plan", schema, returnedPlan ? null : plan)], (_, _, _) =>
            returnedPlan ? Task.FromResult(PluginTaskActionResult.Success(plan)) : throw new InvalidOperationException("声明计划不应触发回调"));
        ((IPluginTaskActionApi)scope).Register([new("complete", "Complete")], (_, _, _) =>
        {
            completed.TrySetResult();
            return Task.FromResult(PluginTaskActionResult.Success());
        });
        fixture.Tasks.Create(new("HostPlan", true, new(TaskTriggerType.WinPilotStartup), [
            new(TaskActionType.Extension, PluginId: "host", ActionId: "plan", Parameters: parameters, ContinueOnFailure: true),
            new(TaskActionType.Extension, PluginId: "host", ActionId: "complete")]));
        fixture.Tasks.RunNow("HostPlan");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var errors = fixture.Logger.Exceptions.ToArray();
        return errors.Length == 0 ? null : Assert.Single(errors);
    }

    private static JsonElement HostPlanJsonValue(string source)
    {
        using var document = JsonDocument.Parse(source);
        return document.RootElement.Clone();
    }
}
