using WinPilot.Features.Plugins;

namespace WinPilot.Features.Notifications.CommandBots.Commands;

internal static class CommandHelpReply
{
    internal static async Task SendAsync(string prefix, IReadOnlyList<RegisteredPluginCommand> plugins,
        Func<string, int, Task> replyText, Func<byte[], int, Task> replyImage,
        ILogger logger, string channel, CancellationToken token, IEnumerable<string>? diagnosticSecrets = null)
    {
        var commands = CommandProcessor.Definitions;
        var pageCount = (commands.Count + plugins.Count + CommandListReply.PageSize - 1) / CommandListReply.PageSize;
        var sequence = 0;
        for (var page = 0; page < pageCount; page++)
        {
            token.ThrowIfCancellationRequested();
            var offset = page * CommandListReply.PageSize;
            var corePage = commands.Skip(offset).Take(CommandListReply.PageSize).ToArray();
            var pluginPage = plugins.Skip(Math.Max(0, offset - commands.Count))
                .Take(CommandListReply.PageSize - corePage.Length).ToArray();
            var title = pageCount > 1 ? $"WinPilot 命令帮助（{page + 1}/{pageCount}）" : "WinPilot 命令帮助";
            var stage = "图片生成";
            try
            {
                var image = CommandImageRenderer.RenderHelp(corePage, prefix, pluginPage, title);
                token.ThrowIfCancellationRequested();
                stage = "图片发送";
                await replyImage(image, ++sequence);
                continue;
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets),
                    "{Channel}「帮助」命令{Stage}失败，尝试改用文字回复", channel, stage);
            }
            var text = CommandProcessor.HelpText(prefix, pluginPage, corePage, title);
            foreach (var part in CommandListReply.SplitText(text))
            {
                token.ThrowIfCancellationRequested();
                await replyText(part, ++sequence);
            }
        }
    }
}
