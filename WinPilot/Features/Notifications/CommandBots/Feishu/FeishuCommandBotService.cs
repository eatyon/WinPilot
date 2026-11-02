using WinPilot.Core;
using WinPilot.Features.Desktop;
using WinPilot.Core.SystemStatus;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;
using WinPilot.Features.Tasks;
using WinPilot.Features.Plugins;

namespace WinPilot.Features.Notifications.CommandBots;

public sealed class FeishuCommandBotService : CommandBotService
{
    private readonly ILogger<FeishuCommandBotService> logger;
    private readonly FeishuConnectAsync? connect;

    public FeishuCommandBotService(PortableDataPaths paths, ILogger<FeishuCommandBotService> logger, CommandBotConfiguration configuration,
        DesktopScreenshotService? screenshots = null, ISystemResourceMonitor? systemMonitor = null,
        ProgramTagService? tags = null, IProgramManager? programs = null, TaskService? tasks = null, PluginCommandRegistry? pluginCommands = null)
        : base(paths, configuration, logger, "飞书", screenshots, systemMonitor, tags, programs, tasks, pluginCommands)
    {
        this.logger = logger;
    }

    internal FeishuCommandBotService(PortableDataPaths paths, ILogger<FeishuCommandBotService> logger, CommandBotConfiguration configuration,
        FeishuConnectAsync connect)
        : this(paths, logger, configuration)
    {
        this.connect = connect;
    }

    protected override bool IsEnabled(CommandBotSettings settings) => settings.FeishuEnabled;
    protected override string GetPrefix(CommandBotSettings settings) => settings.FeishuPrefix;
    protected override CommandBotSettings WithPrefix(CommandBotSettings settings, string prefix) => settings with { FeishuPrefix = prefix };
    protected override CommandBotSettings WithEnabled(CommandBotSettings settings, bool enabled) => settings with { FeishuEnabled = enabled };
    protected override bool SameConnection(NotificationSettings a, NotificationSettings b) =>
        a.FeishuAppId == b.FeishuAppId && a.FeishuAppSecret == b.FeishuAppSecret;
    protected override void ValidateSettings(NotificationSettings settings) => Validate(settings);

    internal static void Validate(NotificationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FeishuAppId) || string.IsNullOrWhiteSpace(settings.FeishuAppSecret))
            throw new InvalidOperationException("请先填写并保存飞书 AppId、AppSecret");
    }

    private protected override CommandBotReceiver CreateReceiver() =>
        new FeishuCommandBotReceiver(logger, connect ?? FeishuCommandBotTransport.RunAsync, () => Status, SetStatus, Receive);
}
