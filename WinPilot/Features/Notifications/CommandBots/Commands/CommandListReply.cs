namespace WinPilot.Features.Notifications.CommandBots.Commands;

internal static class CommandListReply
{
    internal const int PageSize = 20;

    internal static async Task SendAsync(string title, IReadOnlyList<string> names, string usage,
        Func<string, int, Task> replyText, Func<byte[], int, Task>? replyImage,
        ILogger logger, string channel, CancellationToken token)
    {
        var pageCount = (names.Count + PageSize - 1) / PageSize;
        var sequence = 0;
        for (var page = 0; page < pageCount; page++)
        {
            token.ThrowIfCancellationRequested();
            var items = names.Skip(page * PageSize).Take(PageSize).ToArray();
            var heading = pageCount > 1 ? $"{title}（{page + 1}/{pageCount}）" : title;
            if (replyImage is not null)
            {
                try
                {
                    var image = CommandImageRenderer.RenderList(heading, items, usage);
                    await replyImage(image, ++sequence);
                    continue;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "{Channel} 命令列表图片生成或发送失败，尝试文字回复", channel);
                }
            }
            var text = $"{heading}：\n" + string.Join("\n", items) +
                $"\n\n{usage}\n命令与名称之间的空格可省略";
            foreach (var part in SplitText(text))
            {
                token.ThrowIfCancellationRequested();
                await replyText(part, ++sequence);
            }
        }
    }

    internal static IEnumerable<string> SplitText(string text)
    {
        const int limit = 1500;
        while (text.Length > limit)
        {
            var length = text.LastIndexOf('\n', limit - 1, limit);
            if (length <= 0) length = limit;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            yield return text[..length];
            text = text[length..].TrimStart('\n');
        }
        if (text.Length > 0) yield return text;
    }
}
