using System.Text.Json;
using WinPilot.Features.Notifications;
using WinPilot.Features.Plugins;

namespace WinPilot.Test;

public sealed class PluginSettingOptionSourcesTest
{
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
        const string configuration = """{"entries":[{"channel":"qqbot"},{"channel":"windows"}]}""";

        using var selected = JsonDocument.Parse(PluginSettingOptionSources.WithSelectedOptions(definition, configuration));
        var options = selected.RootElement[0].GetProperty("item").GetProperty("fields")[0].GetProperty("options");
        Assert.Equal(2, options.GetArrayLength());
        Assert.Equal("QQBot", options[0].GetProperty("label").GetString());
        Assert.Equal("Windows 通知", options[1].GetProperty("label").GetString());
    }
}
