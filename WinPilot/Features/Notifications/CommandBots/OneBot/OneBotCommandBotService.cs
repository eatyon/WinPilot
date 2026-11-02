using Microsoft.AspNetCore.WebUtilities;
using WinPilot.Core;
using WinPilot.Features.Desktop;
using WinPilot.Core.SystemStatus;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;
using WinPilot.Features.Tasks;
using WinPilot.Features.Plugins;

namespace WinPilot.Features.Notifications.CommandBots;

public sealed record OneBotCommandBotOptions(bool Enabled, string WebSocketUrl, string Token, string Prefix = "");

public sealed class OneBotCommandBotService : CommandBotService
{
    private readonly PortableDataPaths paths;
    private readonly ILogger<OneBotCommandBotService> logger;
    private readonly OneBotConnectAsync? connect;
    private Func<CommandBotMessage, string, byte[]?, CancellationToken, Task>? reply;

    public OneBotCommandBotService(PortableDataPaths paths, ILogger<OneBotCommandBotService> logger, CommandBotConfiguration configuration,
        DesktopScreenshotService? screenshots = null, ISystemResourceMonitor? systemMonitor = null,
        ProgramTagService? tags = null, IProgramManager? programs = null, TaskService? tasks = null, PluginCommandRegistry? pluginCommands = null)
        : base(paths, configuration, logger, "OneBot", screenshots, systemMonitor, tags, programs, tasks, pluginCommands)
    {
        this.paths = paths;
        this.logger = logger;
    }

    internal OneBotCommandBotService(PortableDataPaths paths, ILogger<OneBotCommandBotService> logger,
        CommandBotConfiguration configuration, OneBotConnectAsync connect) : this(paths, logger, configuration) => this.connect = connect;

    protected override bool IsEnabled(CommandBotSettings settings) => settings.OneBotEnabled;
    protected override string GetPrefix(CommandBotSettings settings) => settings.OneBotPrefix;
    protected override CommandBotSettings WithPrefix(CommandBotSettings settings, string prefix) => settings with { OneBotPrefix = prefix };
    protected override bool SameConnection(NotificationSettings a, NotificationSettings b) =>
        a.OneBotUserId == b.OneBotUserId && a.OneBotGroupId == b.OneBotGroupId;
    protected override bool SameBotConnection(CommandBotSettings a, CommandBotSettings b) =>
        base.SameBotConnection(a, b) && a.OneBotWebSocketUrl == b.OneBotWebSocketUrl && a.OneBotToken == b.OneBotToken;
    protected override CommandBotSettings WithEnabled(CommandBotSettings settings, bool enabled) => settings with { OneBotEnabled = enabled };
    protected override void ValidateConfiguration(WinPilotSettings settings) => Validate(Options(settings.CommandBots ?? new()));
    private protected override CommandBotReceiver CreateReceiver() =>
        new OneBotCommandBotReceiver(logger, connect ?? RunConnectionAsync, () => Status, SetStatus, message =>
        {
            var sender = Volatile.Read(ref reply);
            Receive(message with { Reply = sender is null ? null : (text, image, token) => sender(message, text, image, token) });
        });

    private Task RunConnectionAsync(OneBotCommandBotOptions options, Action connected,
        Action<System.Text.Json.JsonElement> receive, CancellationToken token) =>
        OneBotCommandBotTransport.RunAsync(options, connected, receive, token, sender => Volatile.Write(ref reply, sender));

    public OneBotCommandBotOptions GetOptions() => Options(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).CommandBots);
    internal static OneBotCommandBotOptions Options(CommandBotSettings settings) =>
        new(settings.OneBotEnabled, settings.OneBotWebSocketUrl ?? "", settings.OneBotToken ?? "", settings.OneBotPrefix);

    public Task SaveAsync(OneBotCommandBotOptions options, CancellationToken token)
    {
        var normalized = options with { WebSocketUrl = (options.WebSocketUrl ?? "").Trim(), Token = (options.Token ?? "").Trim() };
        Validate(normalized);
        return SaveCommandBotSettingsAsync(settings => settings with
        {
            OneBotEnabled = normalized.Enabled, OneBotWebSocketUrl = normalized.WebSocketUrl, OneBotToken = normalized.Token, OneBotPrefix = normalized.Prefix
        }, token);
    }

    internal static void Validate(OneBotCommandBotOptions options)
    {
        if (!options.Enabled) return;
        if (options.Token.Any(char.IsControl)) throw new InvalidOperationException("OneBot WebSocket Token 不能包含控制字符");
        if (!Uri.TryCreate(options.WebSocketUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("请填写完整的 OneBot WebSocket 地址（ws:// 或 wss://）");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            QueryHelpers.ParseQuery(uri.Query).Keys.Any(key => key.Equals("access_token", StringComparison.OrdinalIgnoreCase) || key.Equals("token", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("请在独立 Token 输入框中填写凭据，不要放在 WebSocket 地址中");
        if (uri.AbsolutePath.TrimEnd('/').Equals("/api", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OneBot /api 接口不推送消息，请填写 / 或 /event 接口地址");
    }
}
