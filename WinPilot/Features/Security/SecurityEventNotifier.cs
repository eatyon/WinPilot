using WinPilot.Core;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;

namespace WinPilot.Features.Security;

/// <summary>
/// 统一记录安全事件，并按配置异步发送通知；停止时取消并等待已登记的发送任务。
/// </summary>
public sealed class SecurityEventNotifier : IDisposable, IAsyncDisposable
{
    private readonly PortableDataPaths paths;
    private readonly INotificationService notifications;
    private readonly ILogger<SecurityEventNotifier> logger;
    private readonly object deliveryGate = new();
    private readonly HashSet<Task> deliveries = [];
    private readonly CancellationTokenSource stoppingCancellation = new();
    private readonly CancellationTokenRegistration stoppingRegistration;
    private Task cancellation = Task.CompletedTask;
    private Task? disposal;
    private bool stopping;
    private bool? authConfigured;
    private readonly object authStateGate = new();

    public SecurityEventNotifier(
        PortableDataPaths paths,
        INotificationService notifications,
        ILogger<SecurityEventNotifier> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        this.paths = paths;
        this.notifications = notifications;
        this.logger = logger;
        stoppingRegistration = lifetime?.ApplicationStopping.Register(RequestStop) ?? default;
    }

    public bool ObserveAuthConfiguration(bool configured) => ObserveAuthConfiguration(configured, null);

    /// <summary>
    /// 检测已配置密码变为无效的状态迁移；首次未设置密码不属于配置失效。
    /// </summary>
    internal bool ObserveAuthConfiguration(bool configured, Action? protect)
    {
        var invalidated = false;
        lock (authStateGate)
        {
            invalidated = authConfigured is true && !configured;
            if (invalidated)
            {
                protect?.Invoke();
            }
            authConfigured = configured;
        }
        if (invalidated)
        {
            Record(SecurityEventType.AuthConfigurationInvalid);
        }
        return invalidated;
    }

    /// <summary>
    /// 立即记录日志，并按指定或当前通知设置投递事件；发送失败不向事件调用方传播。
    /// </summary>
    public void Record(
        SecurityEventType type,
        string? sourceIp = null,
        bool? enabled = null,
        SecurityNotificationSettings? notificationSettings = null,
        string? reason = null)
    {
        var occurredAt = DateTimeOffset.Now;
        logger.LogInformation("[安全事件] {Description}", FormatLogMessage(type, sourceIp, enabled, reason));
        try
        {
            var settings = notificationSettings ?? LoadSettings();
            if (ShouldNotify(type, settings))
            {
                var message = new NotificationMessage(
                    FormatNotificationMessage(type, sourceIp, enabled, occurredAt, reason),
                    Event: $"Security.{type}",
                    Timestamp: occurredAt.ToString("O"));
                TaskCompletionSource completion;
                CancellationToken token;
                lock (deliveryGate)
                {
                    if (stopping)
                    {
                        return;
                    }
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    // 先登记再启动发送，确保停止流程不会漏掉刚创建的异步任务。
                    deliveries.Add(completion.Task);
                    token = stoppingCancellation.Token;
                }
                _ = CompleteDeliveryAsync(completion, type, message, settings, token);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "检查安全事件通知设置失败，事件：{Event}", EventName(type));
        }
    }

    public static SecurityNotificationSettings Validate(SecurityNotificationSettings settings)
    {
        NotificationConfigurationValidator.Validate(settings);
        var events = (settings.Events ?? []).Distinct().ToList();
        // 界面把设置和修改密码作为同一选项，持久化时保持两个事件同时选中。
        if (events.Contains(SecurityEventType.PasswordSet) || events.Contains(SecurityEventType.PasswordChanged))
        {
            if (!events.Contains(SecurityEventType.PasswordSet))
            {
                events.Add(SecurityEventType.PasswordSet);
            }
            if (!events.Contains(SecurityEventType.PasswordChanged))
            {
                events.Add(SecurityEventType.PasswordChanged);
            }
        }
        var channel = (settings.Channel ?? "").Trim().ToLowerInvariant();
        return settings with { Events = events, Channel = channel };
    }

    private SecurityNotificationSettings LoadSettings() =>
        WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SecurityNotifications;

    private static bool ShouldNotify(SecurityEventType type, SecurityNotificationSettings settings)
    {
        var selected = settings.Events ?? [];
        var enabled = type is SecurityEventType.PasswordSet or SecurityEventType.PasswordChanged
            ? selected.Contains(SecurityEventType.PasswordSet)
                || selected.Contains(SecurityEventType.PasswordChanged)
            : selected.Contains(type);
        return !string.IsNullOrWhiteSpace(settings.Channel) && enabled;
    }

    private async Task CompleteDeliveryAsync(
        TaskCompletionSource completion,
        SecurityEventType type,
        NotificationMessage message,
        SecurityNotificationSettings settings,
        CancellationToken token)
    {
        try
        {
            await NotifySafelyAsync(type, message, settings, token).ConfigureAwait(false);
        }
        finally
        {
            lock (deliveryGate)
            {
                completion.TrySetResult();
                deliveries.Remove(completion.Task);
            }
        }
    }

    private async Task NotifySafelyAsync(
        SecurityEventType type,
        NotificationMessage message,
        SecurityNotificationSettings settings,
        CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var results = await notifications.SendAsync(message, settings.Channel, token).ConfigureAwait(false);
            foreach (var result in results.Where(result => !result.Succeeded))
            {
                logger.LogWarning("安全事件通知发送失败，事件：{Event}，通知渠道：{Channel}", EventName(type), result.Channel);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "处理安全事件通知时发生异常，事件：{Event}", EventName(type));
        }
    }

    private void RequestStop()
    {
        lock (deliveryGate)
        {
            if (stopping)
            {
                return;
            }
            stopping = true;
            cancellation = CancelDeliveriesAsync();
        }
    }

    private async Task CancelDeliveriesAsync()
    {
        try
        {
            await stoppingCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException exception)
        {
            logger.LogWarning(exception, "停止安全事件通知时，取消回调失败");
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequestStop();
        Task cancelled;
        Task[] pending;
        lock (deliveryGate)
        {
            cancelled = cancellation;
            pending = deliveries.ToArray();
        }
        await cancelled.ConfigureAwait(false);
        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => GetDisposalTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(GetDisposalTask());

    private Task GetDisposalTask()
    {
        TaskCompletionSource completion;
        lock (deliveryGate)
        {
            if (disposal is not null)
            {
                return disposal;
            }
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
        }
        _ = CompleteDisposalAsync(completion);
        return completion.Task;
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync().ConfigureAwait(false);
        stoppingRegistration.Dispose();
        stoppingCancellation.Dispose();
    }

    private static string FormatNotificationMessage(
        SecurityEventType type,
        string? sourceIp,
        bool? enabled,
        DateTimeOffset occurredAt,
        string? reason)
    {
        var lines = new List<string> { "WinPilot 安全事件", $"事件：{EventName(type, enabled)}" };
        if (!string.IsNullOrWhiteSpace(reason)) lines.Add($"原因：{reason}");
        if (!string.IsNullOrWhiteSpace(sourceIp))
        {
            lines.Add($"来源：{sourceIp}");
        }
        lines.Add($"时间：{occurredAt:yyyy-MM-dd HH:mm:ss}");
        return string.Join("\n", lines);
    }

    private static string EventName(SecurityEventType type, bool? enabled = null) =>
        type switch
        {
            SecurityEventType.PasswordSet => "管理员密码已设置",
            SecurityEventType.PasswordChanged => "管理员密码已修改",
            SecurityEventType.LoginSucceeded => "登录成功",
            SecurityEventType.LoginFailed => "登录失败",
            SecurityEventType.LoginSourceLocked => "登录来源已锁定",
            SecurityEventType.SessionInvalidated => "登录会话已失效",
            SecurityEventType.AuthConfigurationInvalid => "密码配置缺失或损坏",
            SecurityEventType.LanAccessChanged => enabled switch { true => "局域网访问已开启", false => "局域网访问已关闭", null => "局域网访问设置已变更" },
            SecurityEventType.SecurityNotificationSettingsChanged => "安全事件通知配置已修改",
            _ => "未知安全事件"
        };

    private static string FormatLogMessage(SecurityEventType type, string? sourceIp, bool? enabled, string? reason)
    {
        var details = new List<string> { EventName(type, enabled) };
        if (!string.IsNullOrWhiteSpace(reason)) details.Add($"原因：{reason}");
        if (!string.IsNullOrWhiteSpace(sourceIp))
        {
            details.Add($"来源 IP：{sourceIp}");
        }
        return string.Join("，", details);
    }
}
