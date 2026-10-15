using WinPilot.Features.Tasks;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class VariableResolverTest
{
    [Fact]
    public void VariableResolver_ReplacesKnownAndKeepsUnknownText()
    {
        var context = new PluginTaskRunContext("task", "test-plugin", "test-trigger", DateTimeOffset.UtcNow,
            new Dictionary<string, object?> { ["$time"] = "12:00", ["$test.message"] = "Hello", ["$test.number"] = 123 });

        var result = VariableResolver.Resolve("你好 $test.message\n数字：$test.number\n时间：$time\n未知：$unknown\n$test.missing\n&unknown.image", context);

        Assert.Equal(string.Join(Environment.NewLine, ["你好 Hello", "数字：123", "时间：12:00", "未知：$unknown", "$test.missing", "&unknown.image"]), result);
    }

    [Fact]
    public void VariableResolverRemovesKnownImageVariableFromNotificationText()
    {
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now,
            new Dictionary<string, object?> { ["&test.image"] = new PluginImageContent([1], "image/png") });

        Assert.Equal($"签到完成{Environment.NewLine}", VariableResolver.Resolve("签到完成\n&test.image", context));
    }

    [Fact]
    public void VariableResolverPreservesUserBlankLines()
    {
        var context = new PluginTaskRunContext("task", null, null, DateTimeOffset.Now,
            new Dictionary<string, object?> { ["$test.value"] = "内容" });

        Assert.Equal($"标题{Environment.NewLine}{Environment.NewLine}内容", VariableResolver.Resolve("标题\n\n$test.value", context));
    }
}
