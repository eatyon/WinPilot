using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class CommandHelpReplyTest
{
    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(35)]
    [InlineData(36)]
    public async Task HelpPagesCountCommandsNotGroupHeadings(int pluginCount)
    {
        var expectedPages = (CommandProcessor.Definitions.Count + pluginCount + CommandListReply.PageSize - 1)
            / CommandListReply.PageSize;
        var heights = new List<int>();
        var sequences = new List<int>();
        await CommandHelpReply.SendAsync("/", Plugins(pluginCount),
            (_, _) => throw new InvalidOperationException("不应发送文字"), (bytes, sequence) =>
            {
                using var stream = new MemoryStream(bytes);
                using var image = Image.FromStream(stream);
                Assert.InRange(image.Width, 1, 8000);
                Assert.InRange(image.Height, 1, 8000);
                heights.Add(image.Height);
                sequences.Add(sequence);
                return Task.CompletedTask;
            }, NullLogger.Instance, "Test", CancellationToken.None);
        Assert.Equal(expectedPages, heights.Count);
        Assert.Equal(Enumerable.Range(1, expectedPages), sequences);
    }

    [Fact]
    public async Task FailedImageFallsBackToOnlyCurrentPageWithGroupAndOrderPreserved()
    {
        var plugins = Plugins(36);
        var expectedCommands = CommandProcessor.Definitions.Select(command => "/" + command.Usage)
            .Concat(plugins.Select(command => "/" + command.Definition.Usage)).ToArray();
        var expectedPages = (expectedCommands.Length + CommandListReply.PageSize - 1) / CommandListReply.PageSize;
        var texts = new List<string>();
        var sequences = new List<int>();
        await CommandHelpReply.SendAsync("/", plugins, (text, sequence) =>
        {
            texts.Add(text);
            sequences.Add(sequence);
            return Task.CompletedTask;
        }, (_, _) => throw new IOException(), NullLogger.Instance, "Test", CancellationToken.None);
        Assert.Equal(expectedPages, texts.Count);
        Assert.Equal(Enumerable.Range(1, expectedPages).Select(page => page * 2), sequences);
        for (var index = 0; index < texts.Count; index++)
        {
            Assert.Contains($"（{index + 1}/{expectedPages}）", texts[index]);
            if (texts[index].Split('\n').Any(line => line.StartsWith("/插件", StringComparison.Ordinal)))
                Assert.Contains("插件甲\n", texts[index]);
            Assert.InRange(texts[index].Split('\n').Count(line => line.StartsWith('/')), 1, CommandListReply.PageSize);
        }
        Assert.Contains("WinPilot\n", texts[0]);
        var all = string.Join('\n', texts);
        var actualCommands = all.Split('\n').Where(line => line.StartsWith('/'))
            .Select(line => line.Split(" — ", 2)[0]);
        Assert.Equal(expectedCommands, actualCommands);
        foreach (var command in plugins)
            Assert.Equal(1, all.Split('\n').Count(line => line.StartsWith($"/{command.Definition.Name} — ")));
        Assert.True(all.IndexOf("/插件15 — ", StringComparison.Ordinal) < all.IndexOf("/插件16 — ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationStopsBeforeNextHelpPage()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CommandHelpReply.SendAsync("", Plugins(CommandListReply.PageSize + 1),
            (_, _) => throw new InvalidOperationException(), (_, _) =>
            {
                calls++;
                cancellation.Cancel();
                return Task.CompletedTask;
            }, NullLogger.Instance, "Test", cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProcessorSendsPagedHelpIncludingRegisteredPluginCommands()
    {
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand);
        using var scope = registry.CreateScope("plugin", "插件甲");
        foreach (var command in Plugins(36)) scope.Register(command.Definition, (_, _) => Task.CompletedTask);
        using var processor = new CommandProcessor("Test", new(), NullLogger.Instance, pluginCommands: registry);
        var replies = 0;
        await processor.ProcessAsync(new("origin", "sender", "帮助")
        {
            Reply = (text, image, _) =>
            {
                Assert.Equal("", text);
                Assert.NotNull(image);
                replies++;
                return Task.CompletedTask;
            }
        }, "", CancellationToken.None);
        Assert.Equal((CommandProcessor.Definitions.Count + 36 + CommandListReply.PageSize - 1)
            / CommandListReply.PageSize, replies);
    }

    private static RegisteredPluginCommand[] Plugins(int count) => Enumerable.Range(1, count)
        .Select(index => new RegisteredPluginCommand("plugin", "插件甲", new PluginCommandDefinition($"插件{index}", "说明"))).ToArray();
}
