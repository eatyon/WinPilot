using System.Text.Json;
using WinPilot.Core;
using WinPilot.Features.Notifications;
using WinPilot.Features.Plugins;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{

    [Fact]
    public void CollectionSchema_AllowsDefaultsAndValidValues()
    {
        const string schema = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"name\":\"name\",\"type\":\"text\",\"required\":true}]}}]";
        const string value = "{\"items\":[{\"__winpilot\":{\"id\":\"item-1\",\"remark\":\"测试\"},\"name\":\"测试\"}]}";

        PluginSettingsSchema.ValidateDefinition(schema);
        PluginSettingsSchema.ValidateStorage(schema, value);
    }

    [Fact]
    public void FixedCollection_AllowsMissingItemLabelButRequiresItemName()
    {
        const string schema = "[{\"name\":\"onlineManagement\",\"type\":\"collection\",\"mode\":\"fixed\",\"items\":[{\"name\":\"connection\",\"fields\":[{\"name\":\"uid\",\"type\":\"text\"}]}]}]";
        const string missingName = "[{\"name\":\"onlineManagement\",\"type\":\"collection\",\"mode\":\"fixed\",\"items\":[{\"fields\":[{\"name\":\"uid\",\"type\":\"text\"}]}]}]";

        PluginSettingsSchema.ValidateDefinition(schema);
        PluginSettingsSchema.ValidateStorage(schema, "{\"onlineManagement\":{\"connection\":{\"uid\":\"123\"}}}");
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(missingName));
    }

    [Fact]
    public void SettingsSchema_AllowsMaskedTextOnly()
    {
        PluginSettingsSchema.ValidateDefinition("""[{"name":"secret","type":"text","masked":true}]""");
        PluginSettingsSchema.ValidateDefinition("""[{"name":"connection","type":"collection","mode":"fixed","items":[{"name":"account","fields":[{"name":"secret","type":"text","masked":true}]}]}]""");
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"secret","type":"text","masked":"true"}]"""));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"enabled","type":"checkbox","masked":true}]"""));
    }

    [Fact]
    public void SettingsSchema_AcceptsDynamicNotificationChannelsWithoutStaticOptions()
    {
        PluginSettingsSchema.ValidateDefinition("""[{"name":"channel","type":"select","optionsSource":"notificationChannels"}]""");
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"channel","type":"select","optionsSource":"unknown"}]"""));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"channel","type":"select","optionsSource":"notificationChannels","options":["qqbot"]}]"""));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"channel","type":"text","optionsSource":"notificationChannels"}]"""));
    }

    [Fact]
    public void DynamicChannelOptions_AreLoadedSeparatelyAndPreserveSavedSelectionLabel()
    {
        const string definition = """[{"name":"channel","type":"select","optionsSource":"notificationChannels"}]""";
        using var unselected = JsonDocument.Parse(PluginSettingOptionSources.WithSelectedOptions(definition, "{}"));
        Assert.False(unselected.RootElement[0].TryGetProperty("options", out _));

        using var selected = JsonDocument.Parse(PluginSettingOptionSources.WithSelectedOptions(definition, """{"channel":"qqbot"}"""));
        Assert.Equal("qqbot", selected.RootElement[0].GetProperty("options")[0].GetProperty("value").GetString());
        Assert.Equal("QQBot", selected.RootElement[0].GetProperty("options")[0].GetProperty("label").GetString());
        var channels = PluginSettingOptionSources.GetOptions("notificationChannels");
        Assert.Equal(("all-enabled", "已启用渠道"), (channels[0].Value, channels[0].Label));
        Assert.Equal(NotificationChannelCatalog.All.Select(channel => (channel.Id.ToLowerInvariant(), channel.DisplayName)),
            channels.Skip(1).Select(option => (option.Value, option.Label)));
        Assert.Equal(channels.Count, channels.Select(option => option.Value).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DynamicChannelOptions_PreserveSelectionsInsideCollections()
    {
        const string definition = """[{"name":"entries","type":"collection","mode":"dynamic","item":{"fields":[{"name":"channel","type":"select","optionsSource":"notificationChannels"}]}}]""";
        const string storage = """{"entries":[{"channel":"qqbot"},{"channel":"windows"}]}""";

        using var selected = JsonDocument.Parse(PluginSettingOptionSources.WithSelectedOptions(definition, storage));
        var options = selected.RootElement[0].GetProperty("item").GetProperty("fields")[0].GetProperty("options");
        Assert.Equal(2, options.GetArrayLength());
        Assert.Equal("QQBot", options[0].GetProperty("label").GetString());
        Assert.Equal("Windows 通知", options[1].GetProperty("label").GetString());
    }

    [Fact]
    public void SettingsSchema_RejectsLegacyKeyIdentifiers()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PluginSettingsSchema.ValidateDefinition("[{\"key\":\"enabled\",\"type\":\"checkbox\",\"label\":\"启用\"}]"));
        Assert.Throws<InvalidOperationException>(() =>
            PluginSettingsSchema.ValidateDefinition("[{\"key\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"key\":\"value\",\"type\":\"text\"}]}}]"));
    }

    [Fact]
    public void CollectionSchema_RejectsInvalidCountAndNestedCollection()
    {
        var invalidCount = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[]}}]";
        var nested = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"name\":\"nested\",\"type\":\"collection\"}]}}]";

        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(invalidCount));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(nested));
    }

    [Fact]
    public void CollectionValue_EnforcesMaxItemsRequiredAndEnabled()
    {
        const string schema = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"name\":\"name\",\"type\":\"text\",\"required\":true}]}}]";
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateStorage(schema, "{\"items\":[{\"id\":\"1\",\"name\":\"\",\"enabled\":true}]}"));
    }

    [Fact]
    public void CollectionValue_RejectsDisabledEnableFieldAndDuplicateIds()
    {
        const string schema = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"name\":\"name\",\"type\":\"text\",\"required\":true}]}}]";

        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateStorage(schema, "{\"items\":[{\"id\":\"1\",\"name\":\"a\"},{\"id\":\"1\",\"name\":\"b\"}]}"));
    }

    [Fact]
    public void FeaturesSchema_AllowsDeclaredFeaturesAndRejectsInvalidDefinitions()
    {
        const string valid = "{\"features\":[{\"id\":\"refresh-log\",\"name\":\"刷新日志\",\"description\":\"重新读取日志文件\",\"mode\":\"action\"},{\"id\":\"sign-in\",\"name\":\"签到\",\"mode\":\"action\"},{\"id\":\"log-page\",\"name\":\"实时日志\",\"mode\":\"page\",\"entry\":\"realtime-log.js\"},{\"id\":\"module-page\",\"name\":\"集成页面\",\"mode\":\"page\",\"entry\":\"page.js\"}]}";

        PluginFeaturesSchema.ValidateDefinition(valid);
        Assert.Throws<InvalidOperationException>(() => PluginFeaturesSchema.ValidateDefinition("{\"features\":[{\"id\":\"removed-modal\",\"name\":\"旧弹窗\",\"mode\":\"modal\"}]}"));
        Assert.Throws<InvalidOperationException>(() => PluginFeaturesSchema.ValidateDefinition("{\"features\":[{\"id\":\"same\",\"name\":\"A\",\"mode\":\"action\"},{\"id\":\"same\",\"name\":\"B\",\"mode\":\"action\"}]}"));
        Assert.Throws<InvalidOperationException>(() => PluginFeaturesSchema.ValidateDefinition("{\"features\":[{\"id\":\"legacy\",\"name\":\"旧模式\",\"mode\":\"execute\"}]}"));
        Assert.Throws<InvalidOperationException>(() => PluginFeaturesSchema.ValidateDefinition("{\"features\":[{\"id\":\"unknown\",\"name\":\"未知\",\"mode\":\"unknown\"}]}"));
        Assert.Throws<InvalidOperationException>(() => PluginFeaturesSchema.ValidateDefinition("{\"features\":[{\"id\":\"invalid-confirm\",\"name\":\"无效确认\",\"mode\":\"action\",\"confirm\":\"false\"}]}"));
        Assert.Throws<InvalidOperationException>(() => PluginFeaturesSchema.ValidateDefinition("{\"features\":[{\"id\":\"missing-entry\",\"name\":\"缺少入口\",\"mode\":\"page\"}]}"));
        Assert.Throws<InvalidOperationException>(() => PluginFeaturesSchema.ValidateDefinition("{\"features\":[{\"id\":\"invalid-entry\",\"name\":\"无效入口\",\"mode\":\"page\",\"entry\":\"../index.html\"}]}"));
        Assert.Throws<InvalidOperationException>(() => PluginFeaturesSchema.ValidateDefinition("{\"features\":[{\"id\":\"invalid-module\",\"name\":\"无效模块\",\"mode\":\"page\",\"entry\":\"index.html\"}]}"));
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("index.js")]
    public void FeaturesSchema_RejectsRemovedIntegratedName(string entry)
    {
        var definition = JsonSerializer.Serialize(new { features = new[] { new { id = "legacy-page", name = "旧页面", mode = "integrated", entry } } });
        Assert.Throws<InvalidOperationException>(() => PluginFeaturesSchema.ValidateDefinition(definition));
    }

}
