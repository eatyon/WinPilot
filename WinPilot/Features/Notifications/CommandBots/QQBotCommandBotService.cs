using System.Text.Json;
using WinPilot.Core;
using WinPilot.Features.Desktop;
using WinPilot.Core.SystemStatus;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;
using WinPilot.Features.Tasks;
using WinPilot.Features.Plugins;

namespace WinPilot.Features.Notifications.CommandBots;

public sealed class QQBotCommandBotService : CommandBotService
{
    private readonly ILogger<QQBotCommandBotService> logger;
    private readonly QQBotConnectAsync? connect;
    private readonly QQBotBindAsync? bind;
    private readonly SemaphoreSlim gateway = new(1, 1);
    private readonly object bindingGate = new();
    private BindingWaiter? binding;
    private CancellationTokenSource? temporaryBinding;
    private bool bindingInProgress;
    private bool connectionActive;

    public QQBotCommandBotService(PortableDataPaths paths, ILogger<QQBotCommandBotService> logger, CommandBotConfiguration configuration,
        DesktopScreenshotService? screenshots = null, ISystemResourceMonitor? systemMonitor = null,
        ProgramTagService? tags = null, IProgramManager? programs = null, TaskService? tasks = null, PluginCommandRegistry? pluginCommands = null)
        : base(paths, configuration, logger, "QQBot", screenshots, systemMonitor, tags, programs, tasks, pluginCommands) => this.logger = logger;

    internal QQBotCommandBotService(PortableDataPaths paths, ILogger<QQBotCommandBotService> logger,
        CommandBotConfiguration configuration, QQBotConnectAsync connect, QQBotBindAsync? bind = null)
        : this(paths, logger, configuration)
    {
        this.connect = connect;
        this.bind = bind;
    }

    protected override bool IsEnabled(CommandBotSettings settings) => settings.QQBotEnabled;
    protected override string GetPrefix(CommandBotSettings settings) => settings.QQBotPrefix;
    protected override CommandBotSettings WithPrefix(CommandBotSettings settings, string prefix) => settings with { QQBotPrefix = prefix };
    protected override CommandBotSettings WithEnabled(CommandBotSettings settings, bool enabled) => settings with { QQBotEnabled = enabled };
    protected override bool SameConnection(NotificationSettings a, NotificationSettings b) =>
        a.QQBotAppId == b.QQBotAppId && a.QQBotClientSecret == b.QQBotClientSecret &&
        a.QQBotOpenId == b.QQBotOpenId && a.QQBotGroupOpenId == b.QQBotGroupOpenId &&
        a.QQBotMessageFormat == b.QQBotMessageFormat;
    protected override void ValidateSettings(NotificationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.QQBotAppId) || string.IsNullOrWhiteSpace(settings.QQBotClientSecret))
            throw new InvalidOperationException("请先填写并保存 QQBot AppID、AppSecret");
    }

    private protected override CommandBotReceiver CreateReceiver() =>
        new QQBotCommandBotReceiver(logger, RunConnectionAsync, OnPayload, () => Status, PublishStatus, Receive);

    private async Task RunConnectionAsync(NotificationSettings settings, QQBotGatewaySession session, Action connected,
        Action<JsonElement> receive, CancellationToken token)
    {
        Task? cancellation;
        lock (bindingGate) cancellation = temporaryBinding?.CancelAsync();
        if (cancellation is not null)
        {
            try { await cancellation; }
            catch (AggregateException) { logger.LogWarning("停止 QQBot 临时连接时发生异常，正在等待连接释放"); }
        }
        await gateway.WaitAsync(token);
        try
        {
            await (connect ?? QQBotCommandBotTransport.RunAsync)(settings, session, () =>
            {
                lock (bindingGate) connectionActive = true;
                connected();
            }, receive, token);
        }
        finally
        {
            lock (bindingGate) connectionActive = false;
            FailBindings();
            gateway.Release();
        }
    }

    private void PublishStatus(CommandBotStatus status)
    {
        SetStatus(status);
        if (status.State == "connected") return;
        FailBindings();
    }

    private void FailBindings()
    {
        lock (bindingGate)
            binding?.Result.TrySetException(new HttpRequestException("QQBot 连接已停止，请恢复连接后重新获取"));
    }

    private void OnPayload(JsonElement payload)
    {
        lock (bindingGate)
            if (binding is { } waiter)
            {
                var id = QQBotBindingService.FindBoundId(payload, waiter.Group, waiter.Code);
                if (!string.IsNullOrWhiteSpace(id)) waiter.Result.TrySetResult(id);
            }
    }

    public async Task<string> BindAsync(NotificationSettings settings, bool group,
        Func<string, CancellationToken, Task> reportCode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (bindingGate)
        {
            if (bindingInProgress) throw new QQBotBindingValidationException("QQBot 正在获取 OpenID，请稍后重试");
            bindingInProgress = true;
        }
        try
        {
            if (Status.Enabled) return await BindConnectedAsync(group, reportCode, token);
            if (!await gateway.WaitAsync(0, token))
                throw new QQBotBindingValidationException("请等待 QQBot 连接成功后再获取 OpenID");
            try
            {
                using var temporary = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (bindingGate)
                {
                    if (Status.Enabled) throw new QQBotBindingValidationException("请等待 QQBot 连接成功后再获取 OpenID");
                    temporaryBinding = temporary;
                }
                try
                {
                    return bind is not null
                        ? await bind(settings, group, reportCode, temporary.Token)
                        : await QQBotBindingService.BindAsync(settings.QQBotAppId, settings.QQBotClientSecret, group, reportCode, temporary.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && temporary.IsCancellationRequested)
                {
                    throw new QQBotBindingValidationException("命令机器人已开启，本次获取已取消；连接成功后请重新获取");
                }
                finally { lock (bindingGate) temporaryBinding = null; }
            }
            finally { gateway.Release(); }
        }
        finally { lock (bindingGate) bindingInProgress = false; }
    }

    private Task<string> BindConnectedAsync(bool group, Func<string, CancellationToken, Task> reportCode, CancellationToken token)
        => QQBotBindingService.WithTimeoutAsync(bindingToken => WaitForBindingAsync(group, reportCode, bindingToken), token, TimeSpan.FromSeconds(60));

    private async Task<string> WaitForBindingAsync(bool group, Func<string, CancellationToken, Task> reportCode, CancellationToken token)
    {
        var waiter = new BindingWaiter(group, Random.Shared.Next(1000, 10000).ToString());
        lock (bindingGate)
        {
            if (!connectionActive || Status.State != "connected") throw new QQBotBindingValidationException("请等待 QQBot 连接成功后再获取 OpenID");
            binding = waiter;
        }
        try
        {
            await reportCode(waiter.Code, token);
            return await waiter.Result.Task.WaitAsync(token);
        }
        finally { lock (bindingGate) binding = null; }
    }

    private sealed record BindingWaiter(bool Group, string Code)
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal delegate Task<string> QQBotBindAsync(NotificationSettings settings, bool group,
    Func<string, CancellationToken, Task> reportCode, CancellationToken token);
