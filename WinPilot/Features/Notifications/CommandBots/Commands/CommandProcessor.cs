using WinPilot.Core;
using WinPilot.Features.Desktop;
using WinPilot.Core.SystemStatus;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;
using WinPilot.Features.Tasks;
using WinPilot.Features.Plugins;

namespace WinPilot.Features.Notifications.CommandBots.Commands;

internal sealed class CommandProcessor(string channel, NotificationSettings settings, ILogger logger,
    DesktopScreenshotService? screenshots = null, ISystemResourceMonitor? systemMonitor = null,
    ProgramTagService? tags = null, IProgramManager? programs = null, TaskService? tasks = null,
    PluginCommandRegistry? pluginCommands = null) : IDisposable
{
    internal enum CommandKind { Help, SystemStatus, DesktopScreenshot, LaunchTag, RunTask }
    internal sealed record CommandDefinition(string Name, string Description, CommandKind Kind, string? Parameters = null)
    {
        internal string Usage => Parameters is null ? Name : $"{Name} {Parameters}";
    }
    internal static readonly IReadOnlyList<CommandDefinition> Definitions =
        Array.AsReadOnly(new[]
        {
            new CommandDefinition("帮助", "查看 WinPilot 与插件注册命令", CommandKind.Help),
            new CommandDefinition("系统状态", "查看 CPU、内存、GPU 和磁盘使用情况", CommandKind.SystemStatus),
            new CommandDefinition("桌面截图", "获取当前电脑全部显示器的桌面截图", CommandKind.DesktopScreenshot),
            new CommandDefinition("启动标签", "不带参数查看标签，指定标签名启动程序", CommandKind.LaunchTag, "[标签名]"),
            new CommandDefinition("运行任务", "不带参数查看任务，指定任务名执行已有任务", CommandKind.RunTask, "[任务名]")
        });
    private HttpClient? client;
    private HttpClient? telegramClient;
    private QQBotNotificationChannel? qqbot;
    private FeishuNotificationChannel? feishu;

    internal static bool IsSupportedCommand(string command) => FindCommand(command) is not null;
    internal static string? GetCommandName(string command) => FindCommand(command)?.Name;

    internal static string FormatCommandForLog(string name, string content)
    {
        if (name == "*") return string.Concat(content.Trim().Select(character => char.IsControl(character) ? ' ' : character));
        var arguments = content.Trim()[name.Length..].Trim();
        var text = arguments.Length == 0 ? name : $"{name} {arguments}";
        return string.Concat(text.Select(character => char.IsControl(character) ? ' ' : character));
    }

    private static CommandDefinition? FindCommand(string command) =>
        Definitions.FirstOrDefault(item => string.Equals(command.Trim(), item.Name, StringComparison.OrdinalIgnoreCase) ||
            (item.Kind is CommandKind.LaunchTag or CommandKind.RunTask) && command.Trim().StartsWith(item.Name, StringComparison.OrdinalIgnoreCase));

    internal static string HelpText(string prefix, IReadOnlyList<RegisteredPluginCommand>? plugins = null,
        IReadOnlyList<CommandDefinition>? commands = null, string title = "WinPilot 命令帮助") => title + "\n\n" +
        ((commands ?? Definitions).Count > 0 && plugins is { Count: > 0 } ? "WinPilot\n" : "") +
        string.Join("\n", (commands ?? Definitions).Select(item => $"{prefix}{item.Usage} — {item.Description}")) +
        (plugins is null ? "" : string.Concat(plugins.GroupBy(item => item.PluginId).Select(group =>
            "\n\n" + group.First().PluginDisplayName + "\n" +
            string.Join("\n", group.Select(item => $"{prefix}{item.Definition.Usage} — {item.Definition.Description}")))));

    internal async Task ProcessAsync(CommandBotMessage message, string prefix, CancellationToken token)
    {
        var command = FindCommand(message.Content);
        var pluginCommandName = command is null ? pluginCommands?.GetCommandName(message.Content) : null;
        if (command is null && pluginCommandName is null) return;
        var loggedCommand = FormatCommandForLog(command?.Name ?? pluginCommandName!, message.Content);
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        if (command is null)
        {
            var sending = new SemaphoreSlim(1, 1);
            var sequence = 0;
            async Task SendPluginReplyAsync(string content, byte[]? image, int _, CancellationToken sendToken)
            {
                await sending.WaitAsync(sendToken);
                try
                {
                    sendToken.ThrowIfCancellationRequested();
                    await SendAsync(message, content, image, ++sequence, sendToken);
                }
                finally { sending.Release(); }
            }
            try
            {
                var handled = await pluginCommands!.ExecuteAsync(message.Content,
                    replyToken => new PluginCommandReply(SendPluginReplyAsync, replyToken), timeout.Token).WaitAsync(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (handled) logger.LogInformation("{Channel} 插件命令已处理：{Command}", channel, loggedCommand);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                logger.LogWarning("{Channel} 插件命令处理或发送回复超时", channel);
            }
            catch (Exception exception)
            {
                logger.LogDebug("{Channel} 插件命令处理或发送回复失败，异常类型：{ErrorType}", channel, exception.GetType().Name);
            }
            return;
        }
        var text = command.Kind == CommandKind.SystemStatus
            ? "无法获取系统状态，请稍后重试" : "无法获取桌面截图，请确认当前电脑桌面可用";
        try
        {
            if (command.Kind == CommandKind.Help)
            {
                await CommandHelpReply.SendAsync(prefix, pluginCommands?.GetAll() ?? [],
                    (content, sequence) => SendAsync(message, content, null, sequence, timeout.Token),
                    (image, sequence) => SendAsync(message, "", image, sequence, timeout.Token),
                    logger, channel, timeout.Token);
                logger.LogInformation("{Channel} 命令已处理：{Command}", channel, loggedCommand);
                return;
            }
            if (command.Kind == CommandKind.RunTask)
            {
                await new RunTaskCommand(tasks, logger, channel).ExecuteAsync(message.Content.Trim()[command.Name.Length..], prefix,
                    (content, sequence) => SendAsync(message, content, null, sequence, timeout.Token), timeout.Token,
                    (image, sequence) => SendAsync(message, "", image, sequence, timeout.Token));
                logger.LogInformation("{Channel} 命令已处理：{Command}", channel, loggedCommand);
                return;
            }
            if (command.Kind == CommandKind.LaunchTag)
            {
                var argument = message.Content.Trim()[command.Name.Length..];
                await new LaunchTagCommand(tags, programs, logger, channel).ExecuteAsync(argument, prefix,
                    (content, sequence) => SendAsync(message, content, null, sequence, timeout.Token), timeout.Token,
                    (image, sequence) => SendAsync(message, "", image, sequence, timeout.Token));
                logger.LogInformation("{Channel} 命令已处理：{Command}", channel, loggedCommand);
                return;
            }
            var stage = command.Kind == CommandKind.SystemStatus ? "系统状态获取" : "桌面截图获取";
            try
            {
                byte[] image;
                if (command.Kind == CommandKind.SystemStatus)
                {
                    var status = new SystemStatusCommand(systemMonitor).Capture(timeout.Token);
                    var rows = SystemStatusCommand.FormatRows(status);
                    text = SystemStatusCommand.FormatText(rows);
                    stage = "图片生成";
                    image = CommandImageRenderer.RenderStatus(status);
                }
                else image = command.Kind switch
                {
                    CommandKind.DesktopScreenshot => new DesktopScreenshotCommand(screenshots).Capture(),
                    _ => throw new InvalidOperationException("命令类型无效")
                };
                timeout.Token.ThrowIfCancellationRequested();
                stage = "图片发送";
                if (command.Kind == CommandKind.DesktopScreenshot)
                    text = "桌面截图发送失败，请检查渠道权限和网络";
                await SendAsync(message, "", image, 1, timeout.Token);
            }
            catch (Exception exception) when (!timeout.IsCancellationRequested)
            {
                logger.LogWarning("{Channel}「{Command}」命令{Stage}失败，尝试改用文字回复，异常类型：{ErrorType}",
                    channel, command.Name, stage, exception.GetType().Name);
                await SendAsync(message, text, null, 2, timeout.Token);
            }
            logger.LogInformation("{Channel} 命令已处理：{Command}", channel, loggedCommand);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("{Channel}「{Command}」命令处理或发送回复超时", channel, command.Name);
        }
        catch (Exception exception)
        {
            logger.LogWarning("{Channel}「{Command}」命令处理或发送回复失败，异常类型：{ErrorType}", channel, command.Name, exception.GetType().Name);
        }
    }

    private Task SendAsync(CommandBotMessage message, string text, byte[]? image, int sequence, CancellationToken token)
    {
        if (message.Reply is { } reply) return reply(text, image, token);
        if (channel == "Telegram")
            return TelegramNotificationChannel.SendToAsync(telegramClient ??= TelegramTransport.CreateClient(settings, TimeSpan.FromSeconds(30)),
                settings, message.ChatId, text, image, message.ThreadId, token);
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return channel switch
        {
            "QQBot" => (qqbot ??= new(client)).ReplyAsync(settings, message, text, image, sequence, token),
            "飞书" => (feishu ??= new(client)).ReplyAsync(settings, message, text, image, token),
            _ => throw new InvalidOperationException("当前渠道没有可用的回复连接")
        };
    }

    public void Dispose()
    {
        qqbot?.Dispose();
        feishu?.Dispose();
        telegramClient?.Dispose();
        client?.Dispose();
    }
}
