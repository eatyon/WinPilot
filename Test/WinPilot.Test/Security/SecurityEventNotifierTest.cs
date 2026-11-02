using WinPilot.Core;
using Microsoft.Extensions.Logging;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;
using WinPilot.Features.Security;
using WinPilot.Infrastructure;

namespace WinPilot.Test;

public sealed class SecurityEventNotifierTest
{
    [Fact]
    public void DefaultsAndValidation_FollowTheSecurityNotificationContract()
    {
        var settings = new SecurityNotificationSettings();
        Assert.Equal("", settings.Channel);
        Assert.Equal(SecurityEventDefaults.Notified, settings.Events);
        Assert.Throws<InvalidOperationException>(() => SecurityEventNotifier.Validate(new SecurityNotificationSettings { Channel = "unknown" }));
        Assert.Equal("", SecurityEventNotifier.Validate(new SecurityNotificationSettings { Channel = " " }).Channel);
        Assert.Throws<InvalidOperationException>(() => SecurityEventNotifier.Validate(new SecurityNotificationSettings { Channel = "all-enabled" }));
        var validated = SecurityEventNotifier.Validate(new SecurityNotificationSettings { Channel = " Telegram " });
        Assert.Equal("telegram", validated.Channel);
        var passwordEvents = SecurityEventNotifier.Validate(new SecurityNotificationSettings { Events = [SecurityEventType.PasswordChanged] });
        Assert.Contains(SecurityEventType.PasswordSet, passwordEvents.Events);
        Assert.Contains(SecurityEventType.PasswordChanged, passwordEvents.Events);
    }

    [Fact]
    public async Task PasswordNotificationSelection_CoversSettingAndChangingThePassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Security.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            var sender = new RecordingNotificationService();
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var notifier = new SecurityEventNotifier(paths, sender, loggerFactory.CreateLogger<SecurityEventNotifier>());
            new WinPilotSettings { Port = 9123, SecurityNotifications = new SecurityNotificationSettings { Channel = NotificationChannelCatalog.Telegram, Events = [SecurityEventType.PasswordSet] } }.Save(paths.ConfigurationFile);

            notifier.Record(SecurityEventType.PasswordSet);
            notifier.Record(SecurityEventType.PasswordChanged);

            await sender.WaitForCalls(2);
            Assert.Equal(2, sender.Calls.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SpecificChannel_StillUsesNotificationProtocolConfigurationValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Security.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            using var service = new NotificationService(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance);
            var results = await service.SendAsync(new NotificationMessage("event"), channel: "telegram");
            Assert.Single(results);
            Assert.False(results[0].Succeeded);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecordsAllSecurityEventsWithoutSensitiveDataAndDeduplicatesInvalidAuthState()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Security.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            using var provider = new RollingFileLoggerProvider(paths.LogsDirectory);
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            var notifier = new SecurityEventNotifier(paths, new RecordingNotificationService(), loggerFactory.CreateLogger<SecurityEventNotifier>());
            foreach (var type in Enum.GetValues<SecurityEventType>()) notifier.Record(type, "192.168.1.10");
            Assert.False(notifier.ObserveAuthConfiguration(false));
            Assert.False(notifier.ObserveAuthConfiguration(true));
            Assert.True(notifier.ObserveAuthConfiguration(false));
            Assert.False(notifier.ObserveAuthConfiguration(false));
            Assert.False(notifier.ObserveAuthConfiguration(true));
            Assert.True(notifier.ObserveAuthConfiguration(false));

            notifier.Record(SecurityEventType.LanAccessChanged, enabled: true);
            notifier.Record(SecurityEventType.LanAccessChanged, enabled: false);
            var log = File.ReadAllText(Directory.GetFiles(paths.LogsDirectory).Single());
            foreach (var name in new[] { "管理员密码已设置", "管理员密码已修改", "登录成功", "登录失败", "登录来源已锁定",
                "密码配置缺失或损坏", "局域网访问设置已变更", "安全事件通知配置已修改", "局域网访问已开启", "局域网访问已关闭" })
                Assert.Contains($"[安全事件] {name}", log);
            Assert.Contains("[安全事件] 登录来源已锁定，来源 IP：192.168.1.10", log);
            Assert.DoesNotContain("锁定时长", log);
            Assert.Equal(3, Count(log, "[安全事件] 密码配置缺失或损坏"));
            Assert.DoesNotContain("事件标识=", log);
            Assert.DoesNotContain("发生时间=", log);
            Assert.DoesNotContain("本机来源=", log);
            Assert.DoesNotContain("结果=", log);
            Assert.DoesNotContain("password=", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hash=", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("salt=", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sessionid=", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie=", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token=", log, StringComparison.OrdinalIgnoreCase);
            await Task.Delay(50);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("", true, null, 0)]
    [InlineData("qqbot", true, "qqbot", 1)]
    [InlineData("telegram", true, "telegram", 1)]
    [InlineData("qqbot", false, null, 0)]
    public async Task NotificationDelivery_UsesSelectedChannelAndEvent(string channel, bool selectEvent, string? expectedChannel, int expectedCalls)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Security.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            var sender = new RecordingNotificationService();
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var notifier = new SecurityEventNotifier(paths, sender, loggerFactory.CreateLogger<SecurityEventNotifier>());

            new WinPilotSettings { SecurityNotifications = new SecurityNotificationSettings
            {
                Channel = channel,
                Events = selectEvent ? [SecurityEventType.PasswordSet] : []
            } }.Save(paths.ConfigurationFile);
            notifier.Record(SecurityEventType.PasswordSet);
            if (expectedCalls == 0)
                Assert.Empty(sender.Calls);
            else
            {
                await sender.WaitForCalls(expectedCalls);
                Assert.Equal(expectedChannel, Assert.Single(sender.Calls));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SettingsChange_UsesThePreviousNotificationConfigurationAfterDisabling()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Security.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            var sender = new RecordingNotificationService();
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var notifier = new SecurityEventNotifier(paths, sender, loggerFactory.CreateLogger<SecurityEventNotifier>());
            var previous = new SecurityNotificationSettings { Channel = "telegram", Events = [SecurityEventType.SecurityNotificationSettingsChanged] };
            new WinPilotSettings { Port = 9123, SecurityNotifications = new SecurityNotificationSettings() }.Save(paths.ConfigurationFile);

            notifier.Record(SecurityEventType.SecurityNotificationSettingsChanged, notificationSettings: previous);

            await sender.WaitForCalls(1);
            Assert.Equal("telegram", sender.Calls[0]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static int Count(string value, string part) => value.Split(part, StringSplitOptions.None).Length - 1;

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string?> Calls { get; } = [];
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, string? channel, CancellationToken cancellationToken = default)
        {
            lock (Calls) Calls.Add(channel);
            return Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new NotificationChannelResult(channel ?? "all", true)]);
        }
        public async Task WaitForCalls(int expected)
        {
            for (var index = 0; index < 40; index++) { lock (Calls) if (Calls.Count >= expected) return; await Task.Delay(25); }
            Assert.Fail($"Expected {expected} notification calls, received {Calls.Count}");
        }
    }
}
