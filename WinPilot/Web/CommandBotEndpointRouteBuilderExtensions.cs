using System.Text.Json;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.Features.Plugins;
using WinPilot.Features.Security;

namespace WinPilot.Web;

public static class CommandBotEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapCommandBotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/settings/notifications/command-bots", (HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] WinPilot.Core.PortableDataPaths paths) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var settings = WinPilot.Core.WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).CommandBots;
            return Results.Ok(new { telegram = settings.TelegramEnabled, feishu = settings.FeishuEnabled, onebot = settings.OneBotEnabled, qqbot = settings.QQBotEnabled });
        });
        endpoints.MapGet("/api/notifications/command-bots/commands", (HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] PluginCommandRegistry registry) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var groups = new List<CommandGroup>
            {
                new("WinPilot", CommandProcessor.Definitions.Select(command => new CommandItem(command.Usage, command.Description)).ToArray())
            };
            groups.AddRange(registry.GetAll().GroupBy(command => command.PluginId).Select(group =>
                new CommandGroup(group.First().PluginDisplayName,
                    group.Select(command => new CommandItem(command.Definition.Usage, command.Definition.Description)).ToArray())));
            return Results.Ok(groups);
        });
        MapBotEndpoints<TelegramCommandBotService>(endpoints, "telegram");
        MapBotEndpoints<FeishuCommandBotService>(endpoints, "feishu");
        MapBotEndpoints<QQBotCommandBotService>(endpoints, "qqbot");
        MapBotEndpoints<OneBotCommandBotService>(endpoints, "onebot");
        endpoints.MapGet("/api/settings/notifications/command-bots/onebot", (HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] OneBotCommandBotService bot, SafeModeService safeMode) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var settings = bot.GetOptions();
            return Results.Ok(settings);
        });
        endpoints.MapPut("/api/settings/notifications/command-bots/onebot", (OneBotCommandBotOptions options, HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] OneBotCommandBotService bot, SafeModeService safeMode) =>
            ExecuteAsync(() => bot.SaveAsync(options, context.RequestAborted), bot, safeMode));
        return endpoints;
    }

    private static void MapBotEndpoints<T>(IEndpointRouteBuilder endpoints, string channel) where T : CommandBotService
    {
        var configurationRoute = $"/api/settings/notifications/command-bots/{channel}";
        var route = $"/api/notifications/command-bots/{channel}";
        endpoints.MapGet(route + "/status", ([Microsoft.AspNetCore.Mvc.FromServices] T bot) => Results.Ok(bot.Status));
        if (typeof(T) != typeof(OneBotCommandBotService))
        {
            endpoints.MapGet(configurationRoute, (HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] T bot, SafeModeService safeMode) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var settings = bot.GetSettings();
                return Results.Ok(settings);
            });
            endpoints.MapPut(configurationRoute, (EnabledRequest request, HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] T bot, SafeModeService safeMode) =>
                ExecuteAsync(() => request.Prefix is null ? bot.SetEnabledAsync(request.Enabled, context.RequestAborted)
                    : bot.SaveSettingsAsync(new(request.Enabled, request.Prefix), context.RequestAborted), bot, safeMode));
        }
        endpoints.MapPost(route + "/reconnect", (HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] T bot, SafeModeService safeMode) =>
            ExecuteAsync(() => bot.ReconnectAsync(context.RequestAborted), bot, safeMode));
        endpoints.MapGet(route + "/events", async (HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] T bot) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            try
            {
                await foreach (var state in bot.WatchAsync(context.RequestAborted))
                {
                    await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(state, JsonSerializerOptions.Web)}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
    }

    private static async Task<IResult> ExecuteAsync(Func<Task> action, CommandBotService bot, SafeModeService safeMode)
    {
        if (safeMode.Enabled) return Results.Json(new { message = "安全模式下不可修改命令机器人设置" }, statusCode: 403);
        try { await action(); return Results.Ok(bot.Status); }
        catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return Results.Problem(statusCode: 500, title: "命令机器人设置保存失败", detail: "无法保存配置，请检查数据目录权限"); }
    }

    private sealed record EnabledRequest(bool Enabled, string? Prefix = null);
    private sealed record CommandItem(string Usage, string Description);
    private sealed record CommandGroup(string Name, CommandItem[] Commands);
}
