using WinPilot.Core;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class PluginFeatureActionTest
{
    [Fact]
    public void ActionBridge_ExecutesRegisteredHandlerAndReturnsResult()
    {
        using var bridge = new CSharpPluginActionBridge();
        using var registration = bridge.Register("refresh", () => PluginFeatureActionResult.Success("刷新完成"));

        var result = bridge.Execute("refresh");

        Assert.True(result.Succeeded);
        Assert.Equal("刷新完成", result.Message);
    }

    [Fact]
    public void ActionBridge_RejectsDuplicateRegistrationAndRemovesDisposedRegistration()
    {
        using var bridge = new CSharpPluginActionBridge();
        var registration = bridge.Register("refresh", () => PluginFeatureActionResult.Success("刷新完成"));

        Assert.Throws<InvalidOperationException>(() => bridge.Register("refresh", () => PluginFeatureActionResult.Success("重复")));

        registration.Dispose();
        Assert.Throws<InvalidOperationException>(() => bridge.Execute("refresh"));
    }

    [Fact]
    public void ActionBridge_RejectsEmptyMessageAndUseAfterDispose()
    {
        var bridge = new CSharpPluginActionBridge();
        bridge.Register("invalid", () => PluginFeatureActionResult.Failure(" "));

        Assert.Throws<InvalidOperationException>(() => bridge.Execute("invalid"));

        bridge.Dispose();
        Assert.Throws<ObjectDisposedException>(() => bridge.Execute("invalid"));
        Assert.Throws<ObjectDisposedException>(() => bridge.Register("next", () => PluginFeatureActionResult.Success("完成")));
    }
}
