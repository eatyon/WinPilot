using WinPilot.Core;
using WinPilot.Core.Tasks;
using WinPilot.Core.Notifications;
using WinPilot.Features.Plugins;
using WinPilot.Features.Tasks;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class PluginTaskExtensionsTest
{
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

    [Fact]
    public void VariableResolver_ReplacesKnownAndKeepsUnknownText()
    {
        var context = new PluginTaskRunContext("task", "test-plugin", "test-trigger", DateTimeOffset.UtcNow,
            new Dictionary<string, object?> { ["$time"] = "12:00", ["$test.message"] = "Hello", ["$test.number"] = 123 });

        var result = VariableResolver.Resolve("你好 $test.message\n数字：$test.number\n时间：$time\n未知：$unknown\n$test.missing\n&unknown.image", context);

        Assert.Equal(string.Join(Environment.NewLine, ["你好 Hello", "数字：123", "时间：12:00", "未知：$unknown", "$test.missing", "&unknown.image"]), result);
    }

    [Fact]
    public async Task DynamicVariableResolver_RunsOnlyForRegisteredRequestedVariables()
    {
        var calls = 0;
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        ((IPluginNotificationVariableApi)scope).Register([new("$test.summary", "Summary")], (names, _, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["$test.summary"] = "resolved"
            });
        });
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now, new Dictionary<string, object?>());

        Assert.Empty(await registry.ResolveVariablesAsync(["$other.value"], context, CancellationToken.None));
        var result = await registry.ResolveVariablesAsync(["$test.summary"], context, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("resolved", result["$test.summary"]);
        scope.Dispose();
        Assert.Empty(await registry.ResolveVariablesAsync(["$test.summary"], context, CancellationToken.None));
    }

    [Fact]
    public void TaskVariableCatalogCombinesHostAndRegisteredPluginVariables()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test Plugin");
        ((IPluginNotificationVariableApi)scope).Register([
            new("$test.summary", "Summary"),
            new("&test.image", "Image", Type: PluginVariableType.Image)
        ]);

        var catalog = new TaskVariableCatalog(registry);
        var variables = catalog.GetAll();

        Assert.Contains(variables, variable => variable.Name == "$time" && variable.PluginId == "winpilot");
        Assert.Contains(variables, variable => variable.Name == "&screenshot" && variable.Type == PluginVariableType.Image);
        Assert.Contains(variables, variable => variable.Name == "&image=" && variable.PluginId == "winpilot");
        Assert.Contains(variables, variable => variable.Name == "$test.summary" && variable.PluginId == "test-plugin");
        Assert.True(catalog.IsRegisteredImage("&screenshot"));
        Assert.True(catalog.IsRegisteredImage("&test.image"));
        Assert.False(catalog.IsRegisteredImage("&image="));
        Assert.False(catalog.IsRegisteredImage("&unknown.image"));
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
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now,
            new Dictionary<string, object?> { ["&test.image"] = image });

        var (path, temporaryPath) = await TaskService.ResolveNotificationImageAsync("&test.image", context);
        try
        {
            Assert.Equal(path, temporaryPath);
            Assert.NotNull(path);
            Assert.Equal(image.Content, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (temporaryPath is not null) File.Delete(temporaryPath);
        }
    }

    [Fact]
    public void NotificationResultsRejectFailedChannels()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TaskService.ValidateNotificationResults([
            new NotificationChannelResult("Windows", true),
            new NotificationChannelResult("QQBot", false),
            new NotificationChannelResult("Telegram", false)
        ]));

        Assert.Equal("通知发送失败：QQBot、Telegram", exception.Message);
    }

    [Fact]
    public void NotificationResultsRequireAtLeastOneChannel()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TaskService.ValidateNotificationResults([]));

        Assert.Equal("没有可用的通知渠道", exception.Message);
    }

    [Fact]
    public void VariableResolverRemovesKnownImageVariableFromNotificationText()
    {
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now,
            new Dictionary<string, object?> { ["&test.image"] = new PluginImageContent([1], "image/png") });

        Assert.Equal($"签到完成{Environment.NewLine}", VariableResolver.Resolve("签到完成\n&test.image", context));
    }

    [Fact]
    public void VariableResolverPreservesUserBlankLines()
    {
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now,
            new Dictionary<string, object?> { ["$test.value"] = "内容" });

        Assert.Equal($"标题{Environment.NewLine}{Environment.NewLine}内容", VariableResolver.Resolve("标题\n\n$test.value", context));
    }

    [Fact]
    public async Task DirectImageDirectiveExtractsQuotedAbsolutePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"winpilot-test-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [137, 80, 78, 71]);
        try
        {
            var (content, imagePath) = TaskService.ExtractDirectImagePath($"签到完成\n&image=\"{path}\"");
            Assert.Equal("签到完成", content);
            Assert.Equal(path, imagePath);
            var resolved = await TaskService.ResolveNotificationImageAsync(imagePath, new PluginTaskRunContext("task", null, null, DateTimeOffset.Now, new Dictionary<string, object?>()));
            Assert.Equal(path, resolved.ImagePath);
            Assert.Null(resolved.TemporaryImagePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NotificationImageVariableMustBeUsedAlone()
    {
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now,
            new Dictionary<string, object?> { ["&test.image"] = new PluginImageContent([1], "image/png") });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TaskService.ResolveNotificationImageAsync("前缀-&test.image", context));

        Assert.Equal("图片变量必须单独填写，不能与其他文字组合", exception.Message);
    }

    [Fact]
    public async Task UnavailableImageVariableDoesNotFallBackToLiteralText()
    {
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now,
            new Dictionary<string, object?>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TaskService.ResolveNotificationImageAsync("&test.image", context));

        Assert.Equal("图片变量 &test.image 不可用", exception.Message);
    }

    [Fact]
    public async Task ImageVariableRegistrationRejectsTextResult()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        ((IPluginNotificationVariableApi)scope).Register(
            [new PluginVariableDefinition("&test.image", "Image", Type: PluginVariableType.Image)],
            (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["&test.image"] = "not-an-image" }));
        Assert.Equal(PluginVariableType.Image, Assert.Single(registry.GetVariables()).Type);

        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now, new Dictionary<string, object?>());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.ResolveVariablesAsync(["&test.image"], context, CancellationToken.None));

        Assert.Equal("图片变量 &test.image 返回了无效内容", exception.Message);
    }

    [Fact]
    public void ExtensionTrigger_PersistsOnlyStableIdentity()
    {
        var stored = TaskTriggerPersistence.From(new TaskTrigger(TaskTriggerType.Extension, PluginId: "test-plugin", TriggerId: "test-trigger"));

        Assert.Equal("Extension", stored.Type);
        Assert.Equal("test-plugin", stored.PluginId);
        Assert.Equal("test-trigger", stored.TriggerId);
        Assert.Null(stored.Cron);
        Assert.Null(stored.ProcessPaths);
    }

    [Fact]
    public void ScopeDispose_CancelsGenerationAndRemovesRegistrations()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        scope.Register([new("test-trigger", "Test trigger")]);
        Assert.Single(registry.GetTriggers());
        scope.Dispose();
        Assert.Empty(registry.GetTriggers());
    }

    [Fact]
    public async Task ActionRegistration_ExposesHandlerAndReadonlyContext()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        ((IPluginTaskActionApi)scope).Register([new("test-action", "Test action")], (parameters, context, token) => Task.FromResult(PluginTaskActionResult.Success()));
        var action = Assert.Single(registry.GetActions());
        var result = await action.Handler!(new Dictionary<string, object?>(), new("task", "test-plugin", null, DateTimeOffset.UtcNow, new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal(PluginTaskActionResultStatus.Success, result.Status);
    }

    [Fact]
    public void HostActionPlan_RegistersFlatAllowlistedSteps()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        var plan = new PluginHostActionPlan([new(PluginHostActionType.Delay, new Dictionary<string, object?> { ["delaySeconds"] = 1 })]);
        ((IPluginTaskActionApi)scope).Register([new("chain", "Chain", HostActionPlan: plan)]);
        Assert.Same(plan, Assert.Single(registry.GetActions()).HostActionPlan);
    }

    [Fact]
    public void HostActionPlan_RejectsEmptyPlan()
    {
        var registry = new PluginTaskExtensionRegistry(new(), new(), new());
        using var scope = registry.CreateScope("test-plugin", "Test");
        Assert.Throws<ArgumentException>(() => ((IPluginTaskActionApi)scope).Register([new("chain", "Chain", HostActionPlan: new PluginHostActionPlan([]))]));
    }
}
