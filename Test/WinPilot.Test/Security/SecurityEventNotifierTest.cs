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
    public void LoginFailureDeduplicationRemovesExpiredSourcesWithoutDroppingActiveOnes()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Security.Test", Guid.NewGuid().ToString("N")));
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var notifier = new SecurityEventNotifier(paths, new RecordingNotificationService(), loggerFactory.CreateLogger<SecurityEventNotifier>());
        var field = typeof(SecurityEventNotifier).GetField("loginFailureNotifications", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>)field.GetValue(notifier)!;
        cache["expired"] = DateTimeOffset.UtcNow.AddMinutes(-6);
        cache["active"] = DateTimeOffset.UtcNow;
        notifier.Record(SecurityEventType.LoginFailed, "current", notificationSettings: new SecurityNotificationSettings
        {
            Enabled = true,
            Events = [SecurityEventType.LoginFailed]
        });
        Assert.False(cache.ContainsKey("expired"));
        Assert.True(cache.ContainsKey("active"));
        Assert.True(cache.ContainsKey("current"));
    }

    [Fact]
    public void DefaultsAndValidation_FollowTheSecurityNotificationContract()
    {
        var settings = new SecurityNotificationSettings();
        Assert.Equal(SecurityNotificationDeliveryMode.AllEnabled, settings.DeliveryMode);
        Assert.Equal(SecurityEventDefaults.Notified, settings.Events);
        Assert.Throws<InvalidOperationException>(() => SecurityEventNotifier.Validate(new SecurityNotificationSettings { DeliveryMode = SecurityNotificationDeliveryMode.SpecificChannels }));
        Assert.Throws<InvalidOperationException>(() => SecurityEventNotifier.Validate(new SecurityNotificationSettings { DeliveryMode = (SecurityNotificationDeliveryMode)999 }));

        var validated = SecurityEventNotifier.Validate(new SecurityNotificationSettings { DeliveryMode = SecurityNotificationDeliveryMode.SpecificChannels, Channels = ["Telegram", "webhook", "telegram"] });
        Assert.Equal(["telegram", "webhook"], validated.Channels);
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
            new WinPilotSettings { Port = 9123, SecurityNotifications = new SecurityNotificationSettings { Enabled = true, Events = [SecurityEventType.PasswordSet] } }.Save(paths.ConfigurationFile);

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
            var service = new NotificationService(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance);
            var results = await service.SendAsync(new NotificationMessage("security", "event"), channel: "telegram", ignoreChannelEnabled: true);
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
            foreach (var type in Enum.GetValues<SecurityEventType>()) notifier.Record(type, "192.168.1.10", false, success: false, lockoutDuration: TimeSpan.FromMinutes(5));
            Assert.False(notifier.ObserveAuthConfiguration(false));
            Assert.False(notifier.ObserveAuthConfiguration(true));
            Assert.True(notifier.ObserveAuthConfiguration(false));
            Assert.False(notifier.ObserveAuthConfiguration(false));
            Assert.False(notifier.ObserveAuthConfiguration(true));
            Assert.True(notifier.ObserveAuthConfiguration(false));

            var log = File.ReadAllText(Directory.GetFiles(paths.LogsDirectory).Single());
            foreach (var type in Enum.GetValues<SecurityEventType>()) Assert.Contains($"事件标识=Security.{type}", log);
            Assert.Equal(3, Count(log, "事件标识=Security.AuthConfigurationInvalid"));
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

    [Fact]
    public async Task NotificationDelivery_UsesEnabledChannelsOrSpecificChannelsWithoutBlockingOtherChannels()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Security.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            var sender = new RecordingNotificationService("telegram");
            using var provider = new RollingFileLoggerProvider(paths.LogsDirectory);
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            var notifier = new SecurityEventNotifier(paths, sender, loggerFactory.CreateLogger<SecurityEventNotifier>());

            new WinPilotSettings { Port = 9123, SecurityNotifications = new SecurityNotificationSettings { Enabled = true, Events = [SecurityEventType.PasswordSet] } }.Save(paths.ConfigurationFile);
            notifier.Record(SecurityEventType.PasswordSet);
            await sender.WaitForCalls(1);
            Assert.Equal((string?)null, sender.Calls[0].Channel);
            Assert.False(sender.Calls[0].IgnoreChannelEnabled);

            sender.Calls.Clear();
            new WinPilotSettings { Port = 9123, SecurityNotifications = new SecurityNotificationSettings { Enabled = true, Events = [SecurityEventType.PasswordSet], DeliveryMode = SecurityNotificationDeliveryMode.SpecificChannels, Channels = ["telegram", "webhook"] } }.Save(paths.ConfigurationFile);
            notifier.Record(SecurityEventType.PasswordSet);
            await sender.WaitForCalls(2);
            Assert.All(sender.Calls, call => Assert.True(call.IgnoreChannelEnabled));
            Assert.Equal(["telegram", "webhook"], sender.Calls.Select(call => call.Channel).OrderBy(channel => channel).ToArray());

            sender.Calls.Clear();
            new WinPilotSettings { Port = 9123, SecurityNotifications = new SecurityNotificationSettings { Events = [] } }.Save(paths.ConfigurationFile);
            notifier.Record(SecurityEventType.PasswordSet);
            await Task.Delay(50);
            Assert.Empty(sender.Calls);
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
            var previous = new SecurityNotificationSettings { Enabled = true, Events = [SecurityEventType.SecurityNotificationSettingsChanged], DeliveryMode = SecurityNotificationDeliveryMode.SpecificChannels, Channels = ["telegram"] };
            new WinPilotSettings { Port = 9123, SecurityNotifications = new SecurityNotificationSettings { Enabled = false } }.Save(paths.ConfigurationFile);

            notifier.Record(SecurityEventType.SecurityNotificationSettingsChanged, notificationSettings: previous);

            await sender.WaitForCalls(1);
            Assert.Equal("telegram", sender.Calls[0].Channel);
            Assert.True(sender.Calls[0].IgnoreChannelEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static int Count(string value, string part) => value.Split(part, StringSplitOptions.None).Length - 1;

    private sealed class RecordingNotificationService(string? failedChannel = null) : INotificationService
    {
        public List<(string? Channel, bool IgnoreChannelEnabled)> Calls { get; } = [];
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default, string? channel = null, bool ignoreChannelEnabled = false)
        {
            lock (Calls) Calls.Add((channel, ignoreChannelEnabled));
            return Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new NotificationChannelResult(channel ?? "all", !string.Equals(channel, failedChannel, StringComparison.OrdinalIgnoreCase))]);
        }
        public async Task WaitForCalls(int expected)
        {
            for (var index = 0; index < 40; index++) { lock (Calls) if (Calls.Count >= expected) return; await Task.Delay(25); }
            Assert.Fail($"Expected {expected} notification calls, received {Calls.Count}");
        }
    }
}
