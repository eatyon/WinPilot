using WinPilot.Core;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;
using WinPilot.Features.Notifications.CommandBots;

namespace WinPilot.Web;

public static class NotificationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints, string configurationFile, INotificationService notifications)
    {
        endpoints.MapGet("/api/settings/notifications", () => Results.Ok(WinPilotSettings.LoadAndInitialize(configurationFile).Notifications));
        endpoints.MapPut("/api/settings/notifications", async (NotificationSettings settings, ILoggerFactory loggerFactory, CommandBotConfiguration bots, HttpContext context) =>
        {
            var logger = loggerFactory.CreateLogger("WinPilot.Notifications");
            try
            {
                ValidateSendTarget("onebot", settings);
                ValidateSendTarget("qqbot", settings);
                await bots.SaveNotificationsAsync(configurationFile, _ => settings, context.RequestAborted);
                logger.LogInformation("通知设置已保存");
                return Results.Ok(settings);
            }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
            catch (Exception exception) { logger.LogError(exception, "通知设置保存失败"); return Results.Problem(statusCode: 500, title: "通知设置保存失败", detail: "无法保存通知设置"); }
        });
        endpoints.MapPatch("/api/settings/notifications/{channel}", async (string channel, NotificationSettings settings, ILoggerFactory loggerFactory, CommandBotConfiguration bots, HttpContext context) =>
        {
            var logger = loggerFactory.CreateLogger("WinPilot.Notifications");
            try
            {
                ValidateSendTarget(channel.ToLowerInvariant(), settings);
                var merged = await bots.SaveNotificationsAsync(configurationFile, existing => channel.ToLowerInvariant() switch
                {
                    "windows" => existing with { WindowsEnabled = settings.WindowsEnabled },
                    "webhook" => existing with { WebhookEnabled = settings.WebhookEnabled, WebhookUrl = settings.WebhookUrl, WebhookSendTo = settings.WebhookSendTo },
                    "telegram" => existing with { TelegramEnabled = settings.TelegramEnabled, TelegramBotToken = settings.TelegramBotToken, TelegramChatId = settings.TelegramChatId, TelegramApiBaseUrl = settings.TelegramApiBaseUrl, TelegramProxyUrl = settings.TelegramProxyUrl, TelegramProxyEnabled = settings.TelegramProxyEnabled },
                    "workweixin" => existing with { WorkWeixinEnabled = settings.WorkWeixinEnabled, WorkWeixinWebhookUrl = settings.WorkWeixinWebhookUrl },
                    "feishu" => existing with { FeishuEnabled = settings.FeishuEnabled, FeishuWebhookUrl = settings.FeishuWebhookUrl, FeishuAppId = settings.FeishuAppId, FeishuAppSecret = settings.FeishuAppSecret },
                    "onebot" => existing with { OneBotEnabled = settings.OneBotEnabled, OneBotEndpoint = settings.OneBotEndpoint, OneBotUserId = settings.OneBotUserId, OneBotGroupId = settings.OneBotGroupId, OneBotToken = settings.OneBotToken, OneBotSendTarget = settings.OneBotSendTarget },
                    "qqbot" => existing with { QQBotEnabled = settings.QQBotEnabled, QQBotAppId = settings.QQBotAppId, QQBotClientSecret = settings.QQBotClientSecret, QQBotOpenId = settings.QQBotOpenId, QQBotGroupOpenId = settings.QQBotGroupOpenId, QQBotSendTarget = settings.QQBotSendTarget, QQBotMessageFormat = settings.QQBotMessageFormat },
                    _ => throw new InvalidOperationException("通知渠道无效")
                }, context.RequestAborted);
                var channelName = channel.ToLowerInvariant() switch
                {
                    "windows" => "Windows",
                    "webhook" => "Webhook",
                    "telegram" => "Telegram",
                    "workweixin" => "企业微信",
                    "feishu" => "飞书",
                    "onebot" => "OneBot",
                    "qqbot" => "QQBot",
                    _ => channel
                };
                logger.LogInformation("通知渠道 {Channel} 设置已保存", channelName);
                return Results.Ok(merged);
            }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { message = exception.Message }); }
            catch (Exception exception) { logger.LogError(exception, "通知设置保存失败，渠道：{Channel}", channel); return Results.Problem(statusCode: 500, title: "通知设置保存失败", detail: "无法保存通知设置"); }
        });
        endpoints.MapPost("/api/settings/notifications/test", async (NotificationTestRequest request, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Channel)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["channel"] = ["请选择通知渠道"] });
            if (string.IsNullOrWhiteSpace(request.Content) && string.IsNullOrWhiteSpace(request.Title)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = ["请输入测试内容"] });
            var results = await notifications.SendAsync(new NotificationMessage(request.Title ?? "WinPilot 测试通知", request.Content, request.ImagePath), context.RequestAborted, request.Channel, ignoreChannelEnabled: true);
            if (results.Count == 0) return Results.BadRequest(new { message = "没有启用的通知渠道" });
            return Results.Ok(results);
        });
        endpoints.MapPost("/api/settings/notifications/qqbot/bind-user", (HttpContext context) => BindQQBot(context, configurationFile, false));
        endpoints.MapPost("/api/settings/notifications/qqbot/bind-group", (HttpContext context) => BindQQBot(context, configurationFile, true));
        return endpoints;
    }
    private static void ValidateSendTarget(string channel, NotificationSettings settings)
    {
        if (channel == "qqbot") QQBotNotificationChannel.ValidateMessageFormat(settings);
        if (channel == "onebot" && settings.OneBotSendTarget != "auto")
            OneBotNotificationChannel.SelectTargets(settings);
        if (channel == "qqbot" && settings.QQBotSendTarget != "auto")
            QQBotNotificationChannel.SelectTargets(settings);
    }

    private static async Task BindQQBot(HttpContext context, string configurationFile, bool group)
    {
        context.Response.ContentType = "text/event-stream";
        var settings = WinPilotSettings.LoadAndInitialize(configurationFile).Notifications;
        try
        {
            var bot = context.RequestServices.GetRequiredService<QQBotCommandBotService>();
            var id = await bot.BindAsync(settings, group,
                (code, token) => WriteBindingEventAsync(context, new { type = "code", code }, token), context.RequestAborted);
            await WriteBindingEventAsync(context, new { type = "success", openId = id }, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            await WriteBindingEventAsync(context, new { type = "error", message = "获取超时，请重试" }, context.RequestAborted);
        }
        catch (Exception exception)
        {
            await WriteBindingEventAsync(context, new { type = "error", message = exception.Message }, context.RequestAborted);
        }
    }

    private static async Task WriteBindingEventAsync(HttpContext context, object value, CancellationToken token)
    {
        await context.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(value)}\n\n", token);
        await context.Response.Body.FlushAsync(token);
    }
    private sealed record NotificationTestRequest(string? Title, string Content, string? ImagePath, string? Channel);
}
