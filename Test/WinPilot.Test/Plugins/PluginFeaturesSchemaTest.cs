using System.Text.Json;
using WinPilot.Features.Plugins;

namespace WinPilot.Test;

public sealed class PluginFeaturesSchemaTest
{
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
