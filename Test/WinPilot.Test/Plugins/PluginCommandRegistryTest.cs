using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class PluginCommandRegistryTest
{
    private static PluginCommandRegistry Registry() => new(CommandProcessor.IsSupportedCommand);

    [Theory]
    [InlineData("查询", "插件「插件名称」命令「查询」处理失败，异常类型：IOException")]
    [InlineData("*", "插件「插件名称」通配命令处理失败，异常类型：IOException")]
    public async Task FailureLogsUsePluginDisplayName(string name, string expected)
    {
        var logger = new CommandBotTestObserver<PluginCommandRegistry>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, logger);
        using var scope = registry.CreateScope("plugin-id", "插件名称");
        using var registration = scope.Register(new(name, "说明"), (_, _) => throw new IOException());
        var execution = registry.ExecuteAsync("查询", _ => new RecordingReply(), CancellationToken.None);
        if (name == "*") Assert.True(await execution);
        else await Assert.ThrowsAsync<IOException>(() => execution);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Equal(expected, entry.Message);
    }

    [Fact]
    public void WildcardsArePerPluginAndShownWithHostDescriptionAndPrefix()
    {
        var registry = Registry();
        using var first = registry.CreateScope("first", "插件甲");
        using var second = registry.CreateScope("second", "插件乙");
        using var registration = first.Register(new("*", ""), (_, _) => Task.CompletedTask);
        using var duplicate = first.Register(new("*", "自定义"), (_, _) => throw new InvalidOperationException());
        second.Register(new("*", "自定义"), (_, _) => Task.CompletedTask);
        Assert.Equal(2, registry.GetAll().Count);
        Assert.All(registry.GetAll(), command => Assert.Equal("处理未匹配的命令", command.Definition.Description));
        Assert.Contains("插件甲\n/* — 处理未匹配的命令", CommandProcessor.HelpText("/", registry.GetAll()));
        Assert.Contains("插件乙\n/* — 处理未匹配的命令", CommandProcessor.HelpText("/", registry.GetAll()));
        Assert.Equal("*", registry.GetCommandName("任意命令 参数"));
        Assert.Null(registry.GetCommandName("帮助"));
        Assert.Null(registry.GetCommandName(" "));
        Assert.Throws<ArgumentException>(() => first.Register(new("*", "", "参数"), (_, _) => Task.CompletedTask));
        registration.Dispose();
        using var replacement = first.Register(new("*", ""), (_, _) => Task.CompletedTask);
        duplicate.Dispose();
        Assert.Equal(2, registry.GetAll().Count);
        using var reloaded = registry.CreateScope("first", "新插件甲");
        reloaded.Register(new("*", ""), (_, _) => Task.CompletedTask);
        first.Dispose();
        replacement.Dispose();
        Assert.Equal(2, registry.GetAll().Count);
        Assert.Contains(registry.GetAll(), command => command.PluginDisplayName == "新插件甲");
    }

    [Fact]
    public async Task ExplicitCommandsNeverBroadcastEvenWhenTheyFail()
    {
        var registry = Registry();
        using var scope = registry.CreateScope("plugin", "插件");
        var broadcasts = 0;
        scope.Register(new("*", ""), (_, _) => { Interlocked.Increment(ref broadcasts); return Task.CompletedTask; });
        scope.Register(new("明确命令", "说明"), (_, _) => throw new IOException());
        await Assert.ThrowsAsync<IOException>(() => registry.ExecuteAsync("明确命令", _ => new RecordingReply(), CancellationToken.None));
        Assert.False(await registry.ExecuteAsync("帮助", _ => throw new InvalidOperationException(), CancellationToken.None));
        Assert.Equal(0, broadcasts);
    }

    [Fact]
    public async Task WildcardFailuresDoNotStopOtherRepliesAndWholeTextIsPreserved()
    {
        var registry = Registry();
        using var first = registry.CreateScope("first", "插件甲");
        using var second = registry.CreateScope("second", "插件乙");
        using var failed = registry.CreateScope("failed", "故障插件");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Register(new("*", ""), async (context, _) =>
        {
            Assert.Equal("*", context.Name);
            Assert.Equal("自定义  参数", context.Arguments);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await context.Reply.ReplyTextAsync("甲回复");
        });
        second.Register(new("*", ""), async (context, _) =>
        {
            entered.TrySetResult();
            await context.Reply.ReplyTextAsync("乙回复");
        });
        failed.Register(new("*", ""), (_, _) => throw new IOException());
        using var processor = new CommandProcessor("Test", new(), NullLogger.Instance, pluginCommands: registry);
        var replies = new List<string>();
        var activeSends = 0;
        await processor.ProcessAsync(new("origin", "sender", "自定义  参数")
        {
            Reply = async (text, _, _) =>
            {
                Assert.Equal(1, Interlocked.Increment(ref activeSends));
                await Task.Yield();
                replies.Add(text);
                Interlocked.Decrement(ref activeSends);
            }
        }, "", CancellationToken.None);
        Assert.Equal(new[] { "乙回复", "甲回复" }.Order(), replies.Order());
    }

    [Fact]
    public async Task UnloadingUncooperativeWildcardReleasesWaitAndReplyWithoutCancelingOthers()
    {
        var registry = Registry();
        using var first = registry.CreateScope("first", "插件甲");
        using var second = registry.CreateScope("second", "插件乙");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IPluginCommandReply? retained = null;
        first.Register(new("*", ""), async (context, _) =>
        {
            retained = context.Reply;
            entered.TrySetResult();
            try { await release.Task; }
            finally { finished.TrySetResult(); }
        });
        var otherCalls = 0;
        second.Register(new("*", ""), (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref otherCalls);
            return Task.CompletedTask;
        });
        var executing = registry.ExecuteAsync("未知", token => new PluginCommandReply((_, _, _, _) => Task.CompletedTask, token), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            first.Dispose();
            Assert.True(await executing.WaitAsync(TimeSpan.FromSeconds(3)));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => retained!.ReplyTextAsync("晚到回复"));
            Assert.Equal(1, otherCalls);
            Assert.True(await registry.ExecuteAsync("另一个命令", _ => new RecordingReply(), CancellationToken.None));
            Assert.Equal(2, otherCalls);
        }
        finally { release.TrySetResult(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task CallerCancellationStopsWildcardWaitWithoutTimeoutWarning()
    {
        var logger = new CommandBotTestObserver<PluginCommandRegistry>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, logger);
        using var scope = registry.CreateScope("plugin", "插件");
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Register(new("*", ""), async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var executing = registry.ExecuteAsync("未知命令", _ => new RecordingReply(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        Assert.True(await executing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task WildcardReplyCleanupFailureIsLoggedWithoutFailingBroadcast()
    {
        var logger = new CommandBotTestObserver<PluginCommandRegistry>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, logger);
        using var first = registry.CreateScope("first", "插件甲");
        using var second = registry.CreateScope("second", "插件乙");
        var calls = 0;
        first.Register(new("*", ""), (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        second.Register(new("*", ""), (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        Assert.True(await registry.ExecuteAsync("未知命令", _ => new FailingCleanupReply(), CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Message.Contains("通配命令回复清理失败")));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("sensitive-token"));
    }

    [Theory]
    [InlineData("帮助")]
    [InlineData("系统状态")]
    [InlineData("桌面截图")]
    [InlineData("启动标签")]
    [InlineData("启动标签Chrome")]
    [InlineData("运行任务")]
    [InlineData("运行任务Example")]
    public void CoreCommandNamesCannotBeRegistered(string name)
    {
        var logger = new CommandBotTestObserver<PluginCommandRegistry>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, logger);
        using var scope = registry.CreateScope("plugin", "插件");
        using var skipped = scope.Register(new(name, "说明"), (_, _) => throw new InvalidOperationException());
        Assert.Empty(registry.GetAll());
        Assert.Contains(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            entry.Message.Contains(name) && entry.Message.Contains("WinPilot") && entry.Message.Contains("plugin"));
        using var accepted = scope.Register(new("其他命令", "说明"), (_, _) => Task.CompletedTask);
        Assert.Equal("其他命令", Assert.Single(registry.GetAll()).Definition.Name);
    }

    [Fact]
    public async Task DuplicateNamesAreSkippedCaseInsensitivelyAndDisposalReleasesNames()
    {
        var logger = new CommandBotTestObserver<PluginCommandRegistry>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, logger);
        using var first = registry.CreateScope("first", "第一个");
        using var second = registry.CreateScope("second", "第二个");
        var firstCalls = 0;
        var secondCalls = 0;
        var registration = first.Register(new("PluginStatus", "查看状态"), (_, _) => { firstCalls++; return Task.CompletedTask; });
        using var skipped = second.Register(new("pluginstatus", "查看状态"), (_, _) => throw new InvalidOperationException());
        skipped.Dispose();
        skipped.Dispose();
        Assert.Equal("first", Assert.Single(registry.GetAll()).PluginId);
        Assert.Contains(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            entry.Message.Contains("第二个 (second)") && entry.Message.Contains("第一个 (first)"));
        using var other = second.Register(new("OtherStatus", "查看其他状态"), (_, _) => { secondCalls++; return Task.CompletedTask; });
        Assert.True(await registry.ExecuteAsync("pluginstatus", _ => new RecordingReply(), CancellationToken.None));
        Assert.True(await registry.ExecuteAsync("OtherStatus", _ => new RecordingReply(), CancellationToken.None));
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
        registration.Dispose();
        registration.Dispose();
        using var replacement = second.Register(new("PLUGINSTATUS", "查看状态"), (_, _) => Task.CompletedTask);
        Assert.All(registry.GetAll(), command => Assert.Equal("second", command.PluginId));
        Assert.Equal(2, registry.GetAll().Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" two words")]
    [InlineData("two words")]
    [InlineData("a\nb")]
    public void InvalidNamesAreRejected(string name)
    {
        var registry = Registry();
        using var scope = registry.CreateScope("plugin", "插件");
        Assert.Throws<ArgumentException>(() => scope.Register(new(name, "说明"), (_, _) => Task.CompletedTask));
    }

    [Theory]
    [InlineData("PluginStatus", true)]
    [InlineData(" pluginstatus ", true)]
    [InlineData("PluginStatus extra", false)]
    [InlineData("PluginStatusExtra", false)]
    public void ParameterlessCommandsOnlyMatchExactNames(string text, bool expected)
    {
        var registry = Registry();
        using var scope = registry.CreateScope("plugin", "插件");
        scope.Register(new("PluginStatus", "状态"), (_, _) => Task.CompletedTask);
        Assert.Equal(expected, registry.IsSupportedCommand(text));
    }

    [Theory]
    [InlineData(false, "插件任务", "")]
    [InlineData(false, "插件任务 我的 任务", "我的 任务")]
    [InlineData(false, "插件任务\tExample", "Example")]
    [InlineData(true, "插件任务Example", "Example")]
    public async Task ArgumentsArePassedToHandlerWithoutChangingInternalSpaces(bool attached, string text, string expected)
    {
        var registry = Registry();
        using var scope = registry.CreateScope("plugin", "插件");
        string? argument = null;
        scope.Register(new("插件任务", "任务", "[任务名]", attached), (context, _) =>
        {
            argument = context.Arguments;
            Assert.Equal("插件任务", context.Name);
            return Task.CompletedTask;
        });
        Assert.True(await registry.ExecuteAsync(text, _ => new RecordingReply(), CancellationToken.None));
        Assert.Equal(expected, argument);
        Assert.Equal(attached, registry.IsSupportedCommand("插件任务Example"));
    }

    [Fact]
    public async Task LongestMatchingKeywordWins()
    {
        var registry = Registry();
        using var scope = registry.CreateScope("plugin", "插件");
        var matched = "";
        scope.Register(new("查询", "查询", "[参数]", true), (_, _) => { matched = "查询"; return Task.CompletedTask; });
        scope.Register(new("查询状态", "状态"), (_, _) => { matched = "查询状态"; return Task.CompletedTask; });
        await registry.ExecuteAsync("查询状态", _ => new RecordingReply(), CancellationToken.None);
        Assert.Equal("查询状态", matched);
    }

    [Fact]
    public async Task ReloadInvalidatesOldScopeAndOldDisposalDoesNotRemoveNewCommands()
    {
        var registry = Registry();
        using var old = registry.CreateScope("plugin", "旧名称");
        using var oldRegistration = old.Register(new("插件状态", "旧状态"), (_, _) => Task.CompletedTask);
        using var current = registry.CreateScope("plugin", "新名称");
        Assert.Empty(registry.GetAll());
        Assert.Throws<InvalidOperationException>(() => old.Register(new("旧命令", "说明"), (_, _) => Task.CompletedTask));
        using var registration = current.Register(new("插件状态", "新状态"), (_, _) => Task.CompletedTask);
        old.Dispose();
        oldRegistration.Dispose();
        Assert.Equal("新名称", Assert.Single(registry.GetAll()).PluginDisplayName);
        Assert.True(await registry.ExecuteAsync("插件状态", _ => new RecordingReply(), CancellationToken.None));
        current.Dispose();
        Assert.False(registry.IsSupportedCommand("插件状态"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnregisterOrUnloadCancelsActiveHandlerAndReply(bool unload)
    {
        var registry = Registry();
        using var scope = registry.CreateScope("plugin", "插件");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handlerToken = default;
        var registration = scope.Register(new("等待", "等待"), async (_, token) =>
        {
            handlerToken = token;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var executing = registry.ExecuteAsync("等待", _ => new RecordingReply(), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (unload) scope.Dispose(); else registration.Dispose();
        Assert.True(await executing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(handlerToken.IsCancellationRequested);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task NoMatchingCommandDoesNotCreateReplyOrCallHandler()
    {
        var registry = Registry();
        Assert.False(await registry.ExecuteAsync("未注册", _ => throw new InvalidOperationException(), CancellationToken.None));
    }

    [Fact]
    public async Task UnloadInvalidatesReplyEvenWhenHandlerIgnoresCancellation()
    {
        var registry = Registry();
        using var scope = registry.CreateScope("plugin", "插件");
        IPluginCommandReply? retained = null;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Register(new("等待回复", "等待"), async (context, _) =>
        {
            retained = context.Reply;
            await released.Task;
        });
        var sent = 0;
        var executing = registry.ExecuteAsync("等待回复", token => new PluginCommandReply((_, _, _, _) =>
        {
            sent++;
            return Task.CompletedTask;
        }, token), CancellationToken.None);
        scope.Dispose();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retained!.ReplyTextAsync("失效回复"));
            Assert.Equal(0, sent);
        }
        finally { released.TrySetResult(); await executing; }
    }

    [Fact]
    public void OldPluginContextConstructorRemainsUsableAndTrackedScopeIsRemovedOnDisposal()
    {
        var registry = Registry();
        var scope = registry.CreateScope("plugin", "插件");
        using var context = new CSharpPluginContext("plugin", "directory", NullLogger.Instance,
            new PluginStorageContext("data"), new Status(), new(null, null), new(null, null, null),
            new(null, null, new Security()));
        Assert.Null(context.Commands);
        context.Track(scope);
        scope.Register(new("插件状态", "状态"), (_, _) => Task.CompletedTask);
        context.Dispose();
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task ProcessorSendsPluginTextAndImageToOriginalConversationOnlyAndClosesReplyAfterReturn()
    {
        var registry = Registry();
        using var scope = registry.CreateScope("plugin", "插件名称");
        IPluginCommandReply? retained = null;
        scope.Register(new("插件查询", "查询", "[参数]"), async (context, token) =>
        {
            Assert.Equal("我的 参数", context.Arguments);
            Assert.False(token.IsCancellationRequested);
            retained = context.Reply;
            await context.Reply.ReplyTextAsync("插件文字");
            await context.Reply.ReplyImageAsync(new([1, 2, 3], "image/png"));
        });
        var logger = new CommandBotTestObserver();
        using var processor = new CommandProcessor("Test", new(), logger, pluginCommands: registry);
        var replies = new List<(string Text, byte[]? Image)>();
        await processor.ProcessAsync(new("original-chat", "sender", "插件查询 我的 参数")
        {
            Reply = (text, image, _) => { replies.Add((text, image)); return Task.CompletedTask; }
        }, "/", CancellationToken.None);
        Assert.Equal(2, replies.Count);
        Assert.Equal(("插件文字", (byte[]?)null), replies[0]);
        Assert.Equal("", replies[1].Text);
        Assert.Equal(new byte[] { 1, 2, 3 }, replies[1].Image);
        Assert.Contains(logger.Entries, entry => entry.Message == "Test 插件命令已处理：插件查询 我的 参数");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => retained!.ReplyTextAsync("迟到回复"));
    }

    [Fact]
    public async Task ReplySequenceIsSerializedAndInvalidImagesAreRejected()
    {
        var sequences = new List<int>();
        using var reply = new PluginCommandReply(async (_, _, sequence, _) =>
        {
            await Task.Yield();
            sequences.Add(sequence);
        }, CancellationToken.None);
        await Task.WhenAll(reply.ReplyTextAsync("一"), reply.ReplyTextAsync("二"));
        Assert.Equal(new[] { 1, 2 }, sequences);
        await Assert.ThrowsAsync<ArgumentException>(() => reply.ReplyImageAsync(new([], "image/png")));
        await Assert.ThrowsAsync<ArgumentException>(() => reply.ReplyImageAsync(new([1], "text/plain")));
    }

    [Fact]
    public async Task FailedPluginDoesNotProduceAutomaticSuccessReplyOrRetry()
    {
        var logger = new CommandBotTestObserver<PluginCommandRegistry>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, logger);
        using var scope = registry.CreateScope("plugin", "插件");
        var executions = 0;
        scope.Register(new("故障", "故障"), (_, _) => { executions++; throw new IOException("sensitive-token"); });
        using var processor = new CommandProcessor("Test", new(), logger, pluginCommands: registry);
        var replies = 0;
        await processor.ProcessAsync(new("original", "user", "故障")
        {
            Reply = (_, _, _) => { replies++; return Task.CompletedTask; }
        }, "", CancellationToken.None);
        Assert.Equal(1, executions);
        Assert.Equal(0, replies);
        Assert.Single(logger.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("sensitive-token", StringComparison.Ordinal));
    }

    [Fact]
    public void HelpCommandsKeepRegistrationOrderWithinSortedPluginGroups()
    {
        var registry = Registry();
        using var second = registry.CreateScope("second", "B插件");
        using var first = registry.CreateScope("first", "A插件");
        using var removed = first.Register(new("Removed", "说明"), (_, _) => Task.CompletedTask);
        second.Register(new("BStatus", "说明"), (_, _) => Task.CompletedTask);
        first.Register(new("ZStatus", "说明"), (_, _) => Task.CompletedTask);
        removed.Dispose();
        first.Register(new("ATask", "说明"), (_, _) => Task.CompletedTask);
        second.Register(new("ATest", "说明"), (_, _) => Task.CompletedTask);

        var commands = registry.GetAll();
        Assert.Equal(new[] { "ZStatus", "ATask", "BStatus", "ATest" },
            commands.Select(command => command.Definition.Name));
        var text = CommandProcessor.HelpText("", commands);
        Assert.True(text.IndexOf("ZStatus", StringComparison.Ordinal) < text.IndexOf("ATask", StringComparison.Ordinal));
        Assert.True(text.IndexOf("BStatus", StringComparison.Ordinal) < text.IndexOf("ATest", StringComparison.Ordinal));
    }

    [Fact]
    public void HelpTextAndImageGroupActiveCommandsByPlugin()
    {
        var registry = Registry();
        using var first = registry.CreateScope("first", "插件甲");
        using var second = registry.CreateScope("second", "插件乙");
        first.Register(new("甲查询", "查看甲"), (_, _) => Task.CompletedTask);
        second.Register(new("乙任务", "运行乙", "[任务名]", true), (_, _) => Task.CompletedTask);
        var commands = registry.GetAll();
        var text = CommandProcessor.HelpText("/", commands);
        Assert.Contains("WinPilot\n", text);
        Assert.DoesNotContain("WinPilot 本体", text);
        Assert.Contains("插件甲\n/甲查询", text);
        Assert.Contains("插件乙\n/乙任务 [任务名]", text);
        using var stream = new MemoryStream(CommandImageRenderer.RenderHelp(CommandProcessor.Definitions, "/", commands));
        using var image = Image.FromStream(stream);
        Assert.InRange(image.Width, 1, 8000);
        Assert.InRange(image.Height, 1, 8000);
        first.Dispose();
        Assert.DoesNotContain("/甲查询", CommandProcessor.HelpText("/", registry.GetAll()));
    }

    [Theory]
    [InlineData("插件查询", "插件查询", "")]
    [InlineData("*", "/未知 参数", "/")]
    public async Task BotDispatchesRegisteredPluginCommandsAndIgnoresThemAfterUnload(string registeredName, string content, string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), "winpilot-plugin-command-tests", Guid.NewGuid().ToString("N"));
        var paths = PortableDataPaths.Create(directory);
        new WinPilotSettings { Port = 9123 }.Save(paths.ConfigurationFile);
        var registry = Registry();
        using var scope = registry.CreateScope("plugin", "插件");
        var executions = 0;
        scope.Register(new(registeredName, "查询"), async (context, _) =>
        {
            executions++;
            if (registeredName == "*") Assert.Equal("未知 参数", context.Arguments);
            await context.Reply.ReplyTextAsync("插件回复");
        });
        using var configuration = new CommandBotConfiguration();
        var logger = new CommandBotTestObserver();
        await using var bot = new TestBot(paths, configuration, registry, logger);
        try
        {
            await bot.SaveSettingsAsync(new(true, prefix), CancellationToken.None);
            await bot.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var message = new CommandBotMessage("origin", "sender", content)
            {
                Reply = (text, _, _) => { sent.TrySetResult(text); return Task.CompletedTask; }
            };
            if (prefix.Length > 0)
            {
                var previousLogs = logger.Count;
                bot.Emit(message with { Content = "无前缀" });
                Assert.Equal(previousLogs, logger.Count);
                Assert.Equal(0, executions);
            }
            bot.Emit(message);
            Assert.Equal("插件回复", await sent.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            scope.Dispose();
            var previous = logger.Count;
            bot.Emit(message);
            Assert.Equal(previous, logger.Count);
            Assert.Equal(1, executions);
        }
        finally
        {
            await bot.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class FailingCleanupReply : IPluginCommandReply, IDisposable
    {
        public Task ReplyTextAsync(string text) => Task.CompletedTask;
        public Task ReplyImageAsync(PluginImageContent image) => Task.CompletedTask;
        public void Dispose() => throw new IOException("sensitive-token");
    }

    private sealed class RecordingReply : IPluginCommandReply
    {
        public Task ReplyTextAsync(string text) => Task.CompletedTask;
        public Task ReplyImageAsync(PluginImageContent image) => Task.CompletedTask;
    }

    private sealed class Security : IPluginHostSecurityApi { public bool SafeModeEnabled => false; }
    private sealed class Status : IPluginStatus
    {
        public string? StatusJson => null;
        public event Action<string>? StateChanged { add { } remove { } }
        public event Action<int>? SubscriberCountChanged { add { } remove { } }
        public void Publish(string json) { }
        public void Dispose() { }
    }

    private sealed class TestBot(PortableDataPaths paths, CommandBotConfiguration configuration, PluginCommandRegistry registry, CommandBotTestObserver logger)
        : CommandBotService(paths, configuration, logger, "Test", pluginCommands: registry)
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Emit(CommandBotMessage message) => Receive(message);
        protected override bool IsEnabled(CommandBotSettings settings) => settings.TelegramEnabled;
        protected override CommandBotSettings WithEnabled(CommandBotSettings settings, bool enabled) => settings with { TelegramEnabled = enabled };
        protected override string GetPrefix(CommandBotSettings settings) => settings.TelegramPrefix;
        protected override CommandBotSettings WithPrefix(CommandBotSettings settings, string prefix) => settings with { TelegramPrefix = prefix };
        private protected override CommandBotReceiver CreateReceiver() => new Receiver(this, () => Status, SetStatus, Receive);
    }

    private sealed class Receiver(TestBot owner, Func<CommandBotStatus> status, Action<CommandBotStatus> publish,
        Action<CommandBotMessage> receive) : CommandBotReceiver(status, publish, receive)
    {
        public override async Task RunAsync(WinPilotSettings settings, CancellationToken token)
        {
            SetStatus(Status with { State = "connected" });
            owner.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }
}
