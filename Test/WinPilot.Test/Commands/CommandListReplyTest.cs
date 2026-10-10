using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Features.Notifications.CommandBots.Commands;

namespace WinPilot.Test;

public sealed class CommandListReplyTest
{
    [Fact]
    public async Task ManyNamesSendPagedImagesWithIncreasingSequences()
    {
        var names = Enumerable.Range(1, 41).Select(index => $"标签{index}").ToArray();
        var sequences = new List<int>();
        var heights = new List<int>();
        var logger = new CommandBotTestObserver();
        await CommandListReply.SendAsync("已有标签", names, "用法：启动标签 标签名",
            (_, _) => throw new InvalidOperationException("不应发送文字", logger.Entries.LastOrDefault().Exception), (bytes, sequence) =>
            {
                using var stream = new MemoryStream(bytes);
                using var image = Image.FromStream(stream);
                Assert.InRange(image.Width, 1, 8000);
                heights.Add(image.Height);
                sequences.Add(sequence);
                return Task.CompletedTask;
            }, logger, "Test", CancellationToken.None);
        Assert.Equal(new[] { 1, 2, 3 }, sequences);
        Assert.Equal(heights[0], heights[1]);
        Assert.True(heights[2] < heights[1]);
    }

    [Fact]
    public async Task ImageSendFailureFallsBackToSplitTextWithoutLosingNames()
    {
        var names = new[] { new string('长', 2000), "第二个标签" };
        var replies = new List<string>();
        var imageAttempts = 0;
        var sequences = new List<int>();
        var logger = new CommandBotTestObserver();
        var failure = new IOException("模拟发送失败");
        await CommandListReply.SendAsync("已有标签", names, "用法：启动标签 标签名", (text, sequence) =>
        {
            Assert.InRange(text.Length, 1, 1500);
            replies.Add(text);
            sequences.Add(sequence);
            return Task.CompletedTask;
        }, (_, _) =>
        {
            imageAttempts++;
            throw failure;
        }, logger, "Test", CancellationToken.None);
        Assert.Equal(1, imageAttempts);
        Assert.Same(failure, Assert.Single(logger.Entries).Exception);
        Assert.Contains(names[0], string.Concat(replies));
        Assert.Contains(names[1], string.Concat(replies));
        Assert.Equal(Enumerable.Range(2, replies.Count), sequences);
    }

    [Fact]
    public async Task CancellationStopsBeforeNextPage()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CommandListReply.SendAsync("已有任务",
            Enumerable.Repeat("任务", 21).ToArray(), "用法：运行任务 任务名",
            (_, _) => throw new InvalidOperationException("不应发送文字"), (_, _) =>
            {
                count++;
                cancellation.Cancel();
                return Task.CompletedTask;
            }, NullLogger.Instance, "Test", cancellation.Token));
        Assert.Equal(1, count);
    }

    [Fact]
    public void LongNamesWrapAndIncreaseImageHeight()
    {
        using var shortStream = new MemoryStream(CommandImageRenderer.RenderList("已有任务", new[] { "任务" }, "用法：运行任务 任务名"));
        using var longStream = new MemoryStream(CommandImageRenderer.RenderList("已有任务", new[] { new string('长', 100) }, "用法：运行任务 任务名"));
        using var shortImage = Image.FromStream(shortStream);
        using var longImage = Image.FromStream(longStream);
        Assert.Equal(shortImage.Width, longImage.Width);
        Assert.True(longImage.Height > shortImage.Height);
    }
}
