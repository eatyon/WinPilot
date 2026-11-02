using WinPilot.Core;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;
using WinPilot.Features.Notifications.CommandBots;

namespace WinPilot.Web;

public static class NotificationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(
        this IEndpointRouteBuilder endpoints,
        string configurationFile,
        INotificationService notifications)
    {
        endpoints.MapGet("/api/notifications/channels", () =>
            Results.Ok(NotificationChannelCatalog.All
            .Select(channel => new { value = channel.Id, label = channel.DisplayName })));
        endpoints.MapGet("/api/settings/notifications", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var settings = WinPilotSettings.LoadAndInitialize(configurationFile).Notifications;
            return Results.Ok(settings);
        });
        endpoints.MapPatch("/api/settings/notifications/{channel}", async (
            string channel,
            NotificationSettings settings,
            ILoggerFactory loggerFactory,
            CommandBotConfiguration bots,
            HttpContext context) =>
        {
            var logger = loggerFactory.CreateLogger("WinPilot.Notifications");
            try
            {
                ValidateSendTarget(channel.ToLowerInvariant(), settings);
                var merged = await bots.SaveNotificationsAsync(configurationFile, existing =>
                    channel.ToLowerInvariant() switch
                    {
                        "webhook" => existing with { WebhookUrl = settings.WebhookUrl, WebhookSendTo = settings.WebhookSendTo },
                        "telegram" => existing with
                        {
                            TelegramBotToken = settings.TelegramBotToken,
                            TelegramChatId = settings.TelegramChatId,
                            TelegramApiBaseUrl = settings.TelegramApiBaseUrl,
                            TelegramProxyUrl = settings.TelegramProxyUrl,
                            TelegramProxyEnabled = settings.TelegramProxyEnabled
                        },
                        "workweixin" => existing with { WorkWeixinWebhookUrl = settings.WorkWeixinWebhookUrl },
                        "feishu" => existing with
                        {
                            FeishuWebhookUrl = settings.FeishuWebhookUrl,
                            FeishuAppId = settings.FeishuAppId,
                            FeishuAppSecret = settings.FeishuAppSecret
                        },
                        "onebot" => existing with
                        {
                            OneBotEndpoint = settings.OneBotEndpoint,
                            OneBotUserId = settings.OneBotUserId,
                            OneBotGroupId = settings.OneBotGroupId,
                            OneBotToken = settings.OneBotToken,
                            OneBotSendTarget = settings.OneBotSendTarget
                        },
                        "qqbot" => existing with
                        {
                            QQBotAppId = settings.QQBotAppId,
                            QQBotClientSecret = settings.QQBotClientSecret,
                            QQBotOpenId = settings.QQBotOpenId,
                            QQBotGroupOpenId = settings.QQBotGroupOpenId,
                            QQBotSendTarget = settings.QQBotSendTarget,
                            QQBotMessageFormat = settings.QQBotMessageFormat
                        },
                        _ => throw new InvalidOperationException("通知渠道无效")
                    }, context.RequestAborted);
                var channelName = NotificationChannelCatalog.GetDisplayName(channel);
                logger.LogDebug("通知渠道 {Channel} 设置已保存", channelName);
                return Results.Ok(merged);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "通知设置保存失败，渠道：{Channel}", NotificationChannelCatalog.GetDisplayName(channel));
                return Results.Problem(statusCode: 500, title: "通知设置保存失败", detail: "无法保存通知设置");
            }
        });
        endpoints.MapPost("/api/notifications/test", async (NotificationTestRequest request, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Channel))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["channel"] = ["请选择发送渠道"] });
            }
            if (NotificationChannelCatalog.Find(request.Channel) is null)
            {
                return Results.BadRequest(new { message = "通知渠道无效" });
            }
            if (string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = ["请输入测试内容"] });
            }
            var results = await notifications.SendAsync(
                new NotificationMessage(request.Content, request.ImagePath),
                request.Channel,
                context.RequestAborted);
            if (results.Count == 0)
            {
                return Results.BadRequest(new { message = "没有可用的通知渠道" });
            }
            return Results.Ok(results);
        });
        endpoints.MapPost(
            "/api/notifications/qqbot/bind-user",
            (HttpContext context) => BindQQBot(context, configurationFile, false));
        endpoints.MapPost(
            "/api/notifications/qqbot/bind-group",
            (HttpContext context) => BindQQBot(context, configurationFile, true));
        return endpoints;
    }

    private static void ValidateSendTarget(string channel, NotificationSettings settings)
    {
        if (channel == "qqbot")
        {
            QQBotNotificationChannel.ValidateMessageFormat(settings);
        }
        if (channel == "onebot" && settings.OneBotSendTarget != "auto")
        {
            OneBotNotificationChannel.SelectTargets(settings);
        }
        if (channel == "qqbot" && settings.QQBotSendTarget != "auto")
        {
            QQBotNotificationChannel.SelectTargets(settings);
        }
    }

    private static async Task BindQQBot(HttpContext context, string configurationFile, bool group)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        NotificationSettings settings = new();
        try
        {
            settings = WinPilotSettings.LoadAndInitialize(configurationFile).Notifications;
            var bot = context.RequestServices.GetRequiredService<QQBotCommandBotService>();
            var id = await bot.BindAsync(settings, group,
                (code, token) => WriteBindingEventAsync(context, new { type = "code", code }, token), context.RequestAborted);
            await WriteBindingEventAsync(context, new { type = "success", openId = id }, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (QQBotBindingTimeoutException)
        {
            await WriteBindingEventAsync(context, new { type = "error", message = "获取超时，请重试" }, context.RequestAborted);
        }
        catch (QQBotBindingValidationException exception)
        {
            await WriteBindingEventAsync(context, new { type = "error", message = exception.Message }, context.RequestAborted);
        }
        catch (Exception exception)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            var connectionFailure = exception is HttpRequestException or System.Net.WebSockets.WebSocketException or QQBotGatewayClosedException;
            var message = connectionFailure ? "QQBot 连接失败，请检查网络和平台配置后重试" : "获取 OpenID 失败，请检查程序日志";
            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(NotificationEndpointRouteBuilderExtensions).FullName!)
                .LogWarning(
                    NotificationFailureDiagnostics.Redact(exception, NotificationFailureDiagnostics.Secrets(settings)),
                    "QQBot 获取 OpenID 失败，绑定类型：{BindingType}",
                    group ? "群聊" : "私聊");
            await WriteBindingEventAsync(context, new { type = "error", message }, context.RequestAborted);
        }
    }

    private static async Task WriteBindingEventAsync(HttpContext context, object value, CancellationToken token)
    {
        await context.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(value, ResponseJson.Default)}\n\n", token);
        await context.Response.Body.FlushAsync(token);
    }

    private sealed record NotificationTestRequest(string Content, string? ImagePath, string? Channel);
}
