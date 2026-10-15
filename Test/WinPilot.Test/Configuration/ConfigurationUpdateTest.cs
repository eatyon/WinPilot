using System.Text.Json.Nodes;
using WinPilot.Core;
using WinPilot.Features.Notifications;
using WinPilot.Features.Security;

namespace WinPilot.Test;

public sealed class ConfigurationUpdateTest : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "winpilot-configuration-update-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "config.json");

    [Fact]
    public void LockedTemporaryFileReportsSaveAndCleanupFailuresWithoutChangingConfiguration()
    {
        WinPilotSettings.LoadAndInitialize(FilePath);
        var original = File.ReadAllText(FilePath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, "temporary");
        using (var locked = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<AggregateException>(() => WinPilotSettings.Update(FilePath, current => current with { Port = 9124 }));
            Assert.Equal(2, error.InnerExceptions.Count);
            Assert.All(error.InnerExceptions, exception => Assert.IsAssignableFrom<IOException>(exception));
            Assert.Equal(original, File.ReadAllText(FilePath));
            Assert.Equal("temporary", File.ReadAllText(temporary));
        }
        File.Delete(temporary);
        WinPilotSettings.Update(FilePath, current => current with { Port = 9124 });
        Assert.False(File.Exists(temporary));
        Assert.Equal(9124, WinPilotSettings.LoadAndInitialize(FilePath).Port);
    }

    [Fact]
    public void SavedChineseRemainsReadableAndSpecialCharactersRoundTrip()
    {
        const string value = "中文\"引号\"\\路径\n换行<标签>";
        WinPilotSettings.Update(FilePath, current => current with
        {
            Notifications = current.Notifications with { WebhookSendTo = value }
        });
        var content = File.ReadAllText(FilePath);
        Assert.Contains("中文", content);
        Assert.DoesNotContain("\\u", content);
        Assert.Equal(value, WinPilotSettings.LoadAndInitialize(FilePath).Notifications.WebhookSendTo);
    }

    [Fact]
    public void UpdatePersistsNetworkSettingsAndPreservesUnknownConfiguration()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, """{"futureOption":true}""");
        var updated = WinPilotSettings.Update(FilePath, current => current with { Port = 9124, LanAccessEnabled = true });
        Assert.Equal(9124, updated.Port);
        Assert.True(updated.LanAccessEnabled);
        var saved = WinPilotSettings.LoadAndInitialize(FilePath);
        Assert.Equal(9124, saved.Port);
        Assert.True(saved.LanAccessEnabled);
        var root = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        Assert.True(root["futureOption"]!.GetValue<bool>());
        Assert.False(root.ContainsKey("plugins"));
    }

    [Fact]
    public void FirstRunWritesEverySectionWithoutCreatingPluginData()
    {
        var settings = WinPilotSettings.LoadAndInitialize(FilePath);
        var root = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        foreach (var section in new[] { "notifications", "commandBots", "securityNotifications", "updates" })
            Assert.IsType<JsonObject>(root[section]);
        Assert.Equal(9123, settings.Port);
        Assert.False(settings.LanAccessEnabled);
        Assert.False(settings.CommandBots.TelegramEnabled);
        Assert.False(settings.CommandBots.FeishuEnabled);
        Assert.False(settings.CommandBots.OneBotEnabled);
        Assert.False(settings.CommandBots.QQBotEnabled);
        Assert.False(settings.Notifications.WindowsEnabled);
        Assert.Equal("http://127.0.0.1:7890", settings.Notifications.TelegramProxyUrl);
        Assert.False(settings.SafeModeEnabled);
        Assert.Equal("", settings.SecurityNotifications.Channel);
        Assert.Equal(UpdateSource.GitHub, settings.Updates.Source);
        Assert.Equal(UpdateChannel.Stable, settings.Updates.Channel);
        Assert.False(root.ContainsKey("plugins"));
        Assert.Equal("", root["commandBots"]!["OneBotToken"]!.GetValue<string>());
        Assert.Equal("", root["notifications"]!["QQBotClientSecret"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(directory, "PluginData")));
    }

    [Fact]
    public void MissingNetworkSettingsUseDefaultsWithoutRewritingExistingFile()
    {
        Directory.CreateDirectory(directory);
        const string original = """{"futureOption":true}""";
        File.WriteAllText(FilePath, original);
        var settings = WinPilotSettings.LoadAndInitialize(FilePath);
        Assert.Equal(9123, settings.Port);
        Assert.False(settings.LanAccessEnabled);
        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    [Fact]
    public void ExistingFileUsesDefaultsInMemoryAndNextUpdateWritesCompleteSettings()
    {
        Directory.CreateDirectory(directory);
        const string original = """{"port":9456,"safeModeEnabled":true,"notifications":{"QQBotAppId":"app","TelegramProxyEnabled":true},"commandBots":{"OneBotToken":"token","OneBotEnabled":true}}""";
        File.WriteAllText(FilePath, original);
        var loaded = WinPilotSettings.LoadAndInitialize(FilePath);
        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.Equal("", loaded.CommandBots.TelegramPrefix);
        Assert.Equal("", loaded.SecurityNotifications.Channel);
        WinPilotSettings.Update(FilePath, current => current with { LanAccessEnabled = true });
        var saved = WinPilotSettings.LoadAndInitialize(FilePath);
        Assert.True(saved.LanAccessEnabled);
        Assert.Equal(9456, saved.Port);
        Assert.True(saved.SafeModeEnabled);
        Assert.Equal("app", saved.Notifications.QQBotAppId);
        Assert.True(saved.Notifications.TelegramProxyEnabled);
        Assert.True(saved.CommandBots.OneBotEnabled);
        Assert.Equal("token", saved.CommandBots.OneBotToken);
        var root = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        Assert.NotNull(root["securityNotifications"]);
        Assert.False(root.ContainsKey("plugins"));
        Assert.NotNull(root["commandBots"]!["TelegramPrefix"]);
        Assert.NotNull(root["notifications"]!["FeishuAppSecret"]);
    }

    [Fact]
    public async Task ConcurrentUpdatesToDifferentFieldsDoNotOverwriteEachOther()
    {
        WinPilotSettings.LoadAndInitialize(FilePath);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
        {
            switch (index % 5)
            {
                case 0: WinPilotSettings.Update(FilePath, current => current with { Port = current.Port + 1 }); break;
                case 1: WinPilotSettings.Update(FilePath, current => current with { SafeModeEnabled = true }); break;
                case 2: WinPilotSettings.Update(FilePath, current => current with { Notifications = current.Notifications with { QQBotAppId = "app" } }); break;
                case 3: WinPilotSettings.Update(FilePath, current => current with { CommandBots = current.CommandBots with { TelegramPrefix = "/" } }); break;
                case 4: WinPilotSettings.Update(FilePath, current => current with { SecurityNotifications = current.SecurityNotifications with { Channel = "all-enabled" } }); break;
            }
        })));
        var saved = WinPilotSettings.LoadAndInitialize(FilePath);
        Assert.Equal(9143, saved.Port);
        Assert.True(saved.SafeModeEnabled);
        Assert.Equal("app", saved.Notifications.QQBotAppId);
        Assert.Equal("/", saved.CommandBots.TelegramPrefix);
        Assert.Equal("all-enabled", saved.SecurityNotifications.Channel);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"port\":0}")]
    [InlineData("{\"startupMode\":999}")]
    public void InvalidConfigurationIsNotOverwritten(string original)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, original);
        Assert.Throws<InvalidDataException>(() => WinPilotSettings.LoadAndInitialize(FilePath));
        Assert.Throws<InvalidDataException>(() => WinPilotSettings.Update(FilePath, current => current with { Port = 9124 }));
        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    [Fact]
    public void RejectedUpdateDoesNotChangeFile()
    {
        WinPilotSettings.LoadAndInitialize(FilePath);
        var original = File.ReadAllText(FilePath);
        Assert.Throws<InvalidOperationException>(() => WinPilotSettings.Update(FilePath, _ => throw new InvalidOperationException()));
        Assert.Throws<InvalidDataException>(() => WinPilotSettings.Update(FilePath, current => current with { Port = 0 }));
        Assert.Throws<InvalidDataException>(() => WinPilotSettings.Update(FilePath, current => current with { StartupMode = (StartupMode)999 }));
        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("notifications", "OneBotSendTarget", "unknown", "OneBot 发送目标无效")]
    [InlineData("notifications", "QQBotSendTarget", "unknown", "QQBot 发送目标无效")]
    [InlineData("notifications", "QQBotMessageFormat", "unknown", "QQBot 消息格式无效")]
    [InlineData("notifications", "OneBotSendTarget", null, "OneBot 发送目标无效")]
    [InlineData("notifications", "QQBotMessageFormat", null, "QQBot 消息格式无效")]
    [InlineData("securityNotifications", "Channel", "unknown", "安全通知渠道无效")]
    [InlineData("securityNotifications", "Events", "[999]", "安全事件类型无效")]
    public void InvalidNotificationValuesAreRejectedOnLoadAndSaveWithoutChangingFile(string section, string property, string? value, string message)
    {
        WinPilotSettings.LoadAndInitialize(FilePath);
        var original = File.ReadAllText(FilePath);
        var root = JsonNode.Parse(original)!.AsObject();
        root[section]![property] = property == "Events" ? JsonNode.Parse(value!) : JsonValue.Create(value);
        var invalid = root.ToJsonString();
        File.WriteAllText(FilePath, invalid);
        var loadError = Assert.Throws<InvalidDataException>(() => WinPilotSettings.LoadAndInitialize(FilePath));
        Assert.Equal(message, loadError.Message);
        Assert.Throws<InvalidDataException>(() => WinPilotSettings.Update(FilePath, current => current));
        Assert.Equal(invalid, File.ReadAllText(FilePath));

        File.WriteAllText(FilePath, original);
        var submitted = System.Text.Json.JsonSerializer.Deserialize<WinPilotSettings>(invalid,
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        var saveError = Assert.Throws<InvalidDataException>(() => WinPilotSettings.Update(FilePath, _ => submitted));
        Assert.Equal(message, saveError.Message);
        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));

        if (section == "securityNotifications")
            Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => SecurityEventNotifier.Validate(submitted.SecurityNotifications)).Message);
        else if (property == "QQBotMessageFormat")
            Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => QQBotNotificationChannel.ValidateMessageFormat(submitted.Notifications)).Message);
        else if (property == "OneBotSendTarget")
            Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => OneBotNotificationChannel.SelectTargets(submitted.Notifications)).Message);
        else
            Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => QQBotNotificationChannel.SelectTargets(submitted.Notifications)).Message);
    }

    [Theory]
    [InlineData("auto", "text")]
    [InlineData("private", "markdown")]
    [InlineData("group", "text")]
    public void DisabledChannelsWithoutCredentialsLoadWithValidValues(string target, string format)
    {
        WinPilotSettings.Update(FilePath, current => current with
        {
            Notifications = new() { OneBotSendTarget = target, QQBotSendTarget = target, QQBotMessageFormat = format }
        });
        var original = File.ReadAllText(FilePath);
        var loaded = WinPilotSettings.LoadAndInitialize(FilePath);
        Assert.Equal(target, loaded.Notifications.OneBotSendTarget);
        Assert.Equal(target, loaded.Notifications.QQBotSendTarget);
        Assert.Equal(format, loaded.Notifications.QQBotMessageFormat);
        Assert.False(loaded.Notifications.OneBotEnabled);
        Assert.False(loaded.Notifications.QQBotEnabled);
        Assert.Equal("", loaded.Notifications.OneBotUserId);
        Assert.Equal("", loaded.Notifications.QQBotClientSecret);
        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    [Fact]
    public void AllNotificationChannelsAndEventsLoadWithoutChangingSavedValues()
    {
        foreach (var channel in NotificationChannelCatalog.All.Select(item => item.Id).Append(NotificationChannelCatalog.AllEnabled).Append(""))
        {
            var savedChannel = channel.ToUpperInvariant();
            WinPilotSettings.Update(FilePath, current => current with
            {
                SecurityNotifications = new() { Channel = savedChannel, Events = Enum.GetValues<SecurityEventType>() },
                Notifications = current.Notifications with { TelegramProxyUrl = "http://localhost:10809" }
            });
            var original = File.ReadAllText(FilePath);
            var loaded = WinPilotSettings.LoadAndInitialize(FilePath);
            Assert.Equal(savedChannel, loaded.SecurityNotifications.Channel);
            Assert.Equal(Enum.GetValues<SecurityEventType>(), loaded.SecurityNotifications.Events);
            Assert.Equal("http://localhost:10809", loaded.Notifications.TelegramProxyUrl);
            Assert.Equal(original, File.ReadAllText(FilePath));
        }
    }

    [Fact]
    public void NullSectionsUseDefaultsAndAreFilledOnSave()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, """{"notifications":null,"commandBots":null,"securityNotifications":null}""");
        var saved = WinPilotSettings.Update(FilePath, current => current with { LanAccessEnabled = true });
        Assert.NotNull(saved.Notifications);
        Assert.NotNull(saved.CommandBots);
        Assert.NotNull(saved.SecurityNotifications);
        var root = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        foreach (var section in new[] { "notifications", "commandBots", "securityNotifications" })
            Assert.IsType<JsonObject>(root[section]);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
