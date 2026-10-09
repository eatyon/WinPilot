using System.Collections.Concurrent;
using WinPilot.Core;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;

namespace WinPilot.Features.Security;

public sealed class SecurityEventNotifier(PortableDataPaths paths, INotificationService notifications, ILogger<SecurityEventNotifier> logger)
{
    private static readonly TimeSpan LoginFailureNotificationInterval = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> ValidChannels = new(StringComparer.OrdinalIgnoreCase) { "windows", "webhook", "telegram", "workweixin", "feishu", "onebot", "qqbot" };
    private readonly ConcurrentDictionary<string, DateTimeOffset> loginFailureNotifications = new(StringComparer.Ordinal);
    private long nextFailureCleanupUtcTicks;
    private bool? authConfigured;
    private readonly object authStateGate = new();

    public static IReadOnlyList<string> Channels => ["windows", "webhook", "telegram", "workweixin", "feishu", "onebot", "qqbot"];

    public bool ObserveAuthConfiguration(bool configured)
    {
        var invalidated = false;
        lock (authStateGate)
        {
            invalidated = authConfigured is true && !configured;
            authConfigured = configured;
        }
        if (invalidated) Record(SecurityEventType.AuthConfigurationInvalid);
        return invalidated;
    }

    public void Record(SecurityEventType type, string? sourceIp = null, bool? isLoopback = null, bool? success = null, TimeSpan? lockoutDuration = null, bool? enabled = null, bool allowNotification = true, SecurityNotificationSettings? notificationSettings = null)
    {
        var occurredAtUtc = DateTimeOffset.UtcNow;
        logger.LogInformation("安全事件：{EventName}；{Details}", EventName(type), FormatLogDetails(type, occurredAtUtc, sourceIp, isLoopback, success, lockoutDuration, enabled));
        try
        {
            var settings = notificationSettings ?? LoadSettings();
            if (allowNotification && ShouldNotify(type, sourceIp, settings)) _ = NotifySafelyAsync(type, sourceIp, isLoopback, lockoutDuration, enabled, settings);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "检查安全事件通知设置失败，事件：{Event}", EventName(type));
        }
    }

    public static SecurityNotificationSettings Validate(SecurityNotificationSettings settings)
    {
        var events = (settings.Events ?? []).Distinct().ToList();
        if (events.Any(type => !Enum.IsDefined(type))) throw new InvalidOperationException("安全事件类型无效");
        if (events.Contains(SecurityEventType.PasswordSet) || events.Contains(SecurityEventType.PasswordChanged))
        {
            if (!events.Contains(SecurityEventType.PasswordSet)) events.Add(SecurityEventType.PasswordSet);
            if (!events.Contains(SecurityEventType.PasswordChanged)) events.Add(SecurityEventType.PasswordChanged);
        }
        if (!Enum.IsDefined(settings.DeliveryMode)) throw new InvalidOperationException("安全通知投递模式无效");
        var channels = (settings.Channels ?? []).Where(channel => !string.IsNullOrWhiteSpace(channel)).Select(channel => channel.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (channels.Any(channel => !ValidChannels.Contains(channel))) throw new InvalidOperationException("安全通知渠道无效");
        if (settings.DeliveryMode == SecurityNotificationDeliveryMode.SpecificChannels && channels.Length == 0) throw new InvalidOperationException("指定渠道时至少选择一个通知渠道");
        return settings with { Events = events, Channels = channels };
    }

    private SecurityNotificationSettings LoadSettings() => WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SecurityNotifications;

    private bool ShouldNotify(SecurityEventType type, string? sourceIp, SecurityNotificationSettings settings)
    {
        var selected = settings.Events ?? [];
        var enabled = type is SecurityEventType.PasswordSet or SecurityEventType.PasswordChanged
            ? selected.Contains(SecurityEventType.PasswordSet) || selected.Contains(SecurityEventType.PasswordChanged)
            : selected.Contains(type);
        if (!settings.Enabled || !enabled) return false;
        if (type != SecurityEventType.LoginFailed || string.IsNullOrWhiteSpace(sourceIp)) return true;
        var now = DateTimeOffset.UtcNow;
        var nextCleanup = Volatile.Read(ref nextFailureCleanupUtcTicks);
        if (now.UtcTicks >= nextCleanup &&
            Interlocked.CompareExchange(ref nextFailureCleanupUtcTicks, now.AddMinutes(1).UtcTicks, nextCleanup) == nextCleanup)
        {
            foreach (var item in loginFailureNotifications)
                if (now - item.Value >= LoginFailureNotificationInterval)
                    ((ICollection<KeyValuePair<string, DateTimeOffset>>)loginFailureNotifications).Remove(item);
        }
        return !loginFailureNotifications.TryGetValue(sourceIp, out var last) || now - last >= LoginFailureNotificationInterval
            ? loginFailureNotifications.TryAdd(sourceIp, now) || loginFailureNotifications.TryUpdate(sourceIp, now, last)
            : false;
    }

    private async Task NotifySafelyAsync(SecurityEventType type, string? sourceIp, bool? isLoopback, TimeSpan? lockoutDuration, bool? enabled, SecurityNotificationSettings settings)
    {
        try
        {
            var message = new NotificationMessage("WinPilot 安全事件", Describe(type, sourceIp, isLoopback, lockoutDuration, enabled), Event: $"Security.{type}");
            IReadOnlyList<NotificationChannelResult> results;
            if (settings.DeliveryMode == SecurityNotificationDeliveryMode.AllEnabled)
                results = await notifications.SendAsync(message).ConfigureAwait(false);
            else
                results = (await Task.WhenAll((settings.Channels ?? []).Select(channel => notifications.SendAsync(message, channel: channel, ignoreChannelEnabled: true))).ConfigureAwait(false)).SelectMany(result => result).ToArray();
            foreach (var result in results.Where(result => !result.Succeeded)) logger.LogWarning("安全事件通知发送失败，事件：{Event}，通知渠道：{Channel}", EventName(type), result.Channel);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "处理安全事件通知时发生异常，事件：{Event}", EventName(type));
        }
    }

    private static string Describe(SecurityEventType type, string? sourceIp, bool? isLoopback, TimeSpan? lockoutDuration, bool? enabled) => type switch
    {
        SecurityEventType.PasswordSet => "管理员密码已设置",
        SecurityEventType.PasswordChanged => "管理员密码已修改",
        SecurityEventType.LanAccessChanged => enabled == true ? "局域网访问已开启" : "局域网访问已关闭",
        SecurityEventType.AuthConfigurationInvalid => "密码配置缺失或损坏",
        SecurityEventType.LoginSourceLocked => $"登录来源已锁定{FormatSource(sourceIp)}，锁定时长 {lockoutDuration?.TotalMinutes:0} 分钟",
        SecurityEventType.LoginSucceeded => $"登录成功{FormatSource(sourceIp)}",
        SecurityEventType.LoginFailed => $"登录失败{FormatSource(sourceIp)}",
        SecurityEventType.SessionsRevoked => "现有登录会话已全部撤销",
        SecurityEventType.SecurityNotificationSettingsChanged => "安全事件通知配置已修改",
        _ => "安全事件已发生"
    };

    private static string EventName(SecurityEventType type) => type switch
    {
        SecurityEventType.PasswordSet => "管理员密码已设置",
        SecurityEventType.PasswordChanged => "管理员密码已修改",
        SecurityEventType.LoginSucceeded => "登录成功",
        SecurityEventType.LoginFailed => "登录失败",
        SecurityEventType.LoginSourceLocked => "登录来源已锁定",
        SecurityEventType.AuthConfigurationInvalid => "认证配置无效",
        SecurityEventType.LanAccessChanged => "局域网访问设置已变更",
        SecurityEventType.SessionsRevoked => "登录会话已撤销",
        SecurityEventType.SecurityNotificationSettingsChanged => "安全通知设置已修改",
        _ => "未知安全事件"
    };

    private static string FormatLogDetails(SecurityEventType type, DateTimeOffset occurredAtUtc, string? sourceIp, bool? isLoopback, bool? success, TimeSpan? lockoutDuration, bool? enabled)
    {
        var details = new List<string> { $"事件标识=Security.{type}", $"发生时间={occurredAtUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}" };
        if (!string.IsNullOrWhiteSpace(sourceIp)) details.Add($"来源 IP={sourceIp}");
        if (isLoopback is not null) details.Add($"本机来源={(isLoopback == true ? "是" : "否")}");
        if (success is not null) details.Add($"结果={(success == true ? "成功" : "失败")}");
        if (lockoutDuration is not null) details.Add($"锁定时长={lockoutDuration.Value.TotalMinutes:0} 分钟");
        if (enabled is not null) details.Add($"启用状态={(enabled == true ? "开启" : "关闭")}");
        return string.Join("；", details);
    }

    private static string FormatSource(string? sourceIp) => string.IsNullOrWhiteSpace(sourceIp) ? string.Empty : $"，来源 {sourceIp}";
}
