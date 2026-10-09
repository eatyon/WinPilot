using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.Features.Plugins;
using WinPilot.Features.Security;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class AvailableCommandsTest
{
    [Fact]
    public async Task ListUsesRegisteredOrderAndRemovesUnloadedPlugins()
    {
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand);
        using var plugin = registry.CreateScope("test-plugin", "Test Plugin");
        plugin.Register(new("插件状态", "查看状态"), (_, _) => Task.CompletedTask);
        plugin.Register(new("插件任务", "运行配置", "[名称]", true), (_, _) => Task.CompletedTask);
        plugin.Register(new("*", ""), (_, _) => Task.CompletedTask);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<SafeModeService>();
        await using var app = builder.Build();
        app.MapCommandBotEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/settings/command-bots/commands");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var groups = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, groups.GetArrayLength());
        Assert.Equal("WinPilot", groups[0].GetProperty("name").GetString());
        Assert.Equal(CommandProcessor.Definitions.Select(command => command.Usage),
            groups[0].GetProperty("commands").EnumerateArray().Select(command => command.GetProperty("usage").GetString()));
        Assert.Equal("Test Plugin", groups[1].GetProperty("name").GetString());
        Assert.Equal(new[] { "插件状态", "插件任务 [名称]", "*" },
            groups[1].GetProperty("commands").EnumerateArray().Select(command => command.GetProperty("usage").GetString()));
        Assert.Equal("处理未匹配的命令", groups[1].GetProperty("commands")[2].GetProperty("description").GetString());
        plugin.Dispose();
        var remaining = await client.GetFromJsonAsync<JsonElement>("/api/settings/command-bots/commands");
        Assert.Equal(1, remaining.GetArrayLength());
    }
}
