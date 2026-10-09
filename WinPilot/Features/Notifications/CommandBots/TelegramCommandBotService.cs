using System.Globalization;
using WinPilot.Core;
using WinPilot.Features.Desktop;
using WinPilot.Core.SystemStatus;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;
using WinPilot.Features.Tasks;
using WinPilot.Features.Plugins;

namespace WinPilot.Features.Notifications.CommandBots;

public sealed class TelegramCommandBotService(PortableDataPaths paths, ILogger<TelegramCommandBotService> logger,
    CommandBotConfiguration configuration, DesktopScreenshotService? screenshots = null, ISystemResourceMonitor? systemMonitor = null,
    ProgramTagService? tags = null, IProgramManager? programs = null, TaskService? tasks = null, PluginCommandRegistry? pluginCommands = null)
    : CommandBotService(paths, configuration, logger, "Telegram", screenshots, systemMonitor, tags, programs, tasks, pluginCommands)
{
    protected override bool IsEnabled(CommandBotSettings settings) => settings.TelegramEnabled;
    protected override string GetPrefix(CommandBotSettings settings) => settings.TelegramPrefix;
    protected override CommandBotSettings WithPrefix(CommandBotSettings settings, string prefix) => settings with { TelegramPrefix = prefix };
    protected override CommandBotSettings WithEnabled(CommandBotSettings settings, bool enabled) => settings with { TelegramEnabled = enabled };
    protected override void ValidateSettings(NotificationSettings settings) => Validate(settings);
    protected override bool SameConnection(NotificationSettings a, NotificationSettings b) =>
        a.TelegramBotToken == b.TelegramBotToken && a.TelegramChatId == b.TelegramChatId &&
        a.TelegramApiBaseUrl == b.TelegramApiBaseUrl && a.TelegramProxyEnabled == b.TelegramProxyEnabled &&
        a.TelegramProxyUrl == b.TelegramProxyUrl;

    internal static void Validate(NotificationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken)) throw new InvalidOperationException("请先填写并保存 Telegram 机器人 Token");
        if (!long.TryParse(settings.TelegramChatId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId) || chatId == 0)
            throw new InvalidOperationException("请先填写并保存有效的 Telegram 聊天 ID");
        if (!Uri.TryCreate(TelegramTransport.GetBotUrl(settings), UriKind.Absolute, out var api) ||
            (api.Scheme != "http" && api.Scheme != "https")) throw new InvalidOperationException("Telegram API 基础 URL 无效");
        if (settings.TelegramProxyEnabled &&
            (!Uri.TryCreate(settings.TelegramProxyUrl, UriKind.Absolute, out var proxy) ||
             proxy.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")))
            throw new InvalidOperationException("请填写有效的 Telegram 代理地址");
    }

    private protected override CommandBotReceiver CreateReceiver() =>
        new TelegramCommandBotReceiver(logger, () => Status, SetStatus, Receive);
}
