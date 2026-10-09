using System.Globalization;
using System.Text.Json;
using WinPilot.Core;

namespace WinPilot.Features.Notifications.CommandBots;

internal sealed class TelegramCommandBotReceiver(ILogger<TelegramCommandBotService> logger,
    Func<CommandBotStatus> status, Action<CommandBotStatus> publish, Action<CommandBotMessage> receive)
    : CommandBotReceiver(status, publish, receive)
{
    public override async Task RunAsync(WinPilotSettings configuration, CancellationToken token)
    {
        var settings = configuration.Notifications ?? new();
        try
        {
            using var client = TelegramTransport.CreateClient(settings, TimeSpan.FromSeconds(45));
            await ReceiveLoopAsync(settings, client, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            SetStatus(Status with { State = "failed", Error = "Telegram 连接无法初始化，请检查代理和 API 地址" });
        }
    }

    private async Task ReceiveLoopAsync(NotificationSettings settings, HttpClient client, CancellationToken token)
    {
        var botUrl = TelegramTransport.GetBotUrl(settings);
        long offset = 0;
        var initialized = false;
        string? botMention = null;
        var retrySeconds = 2;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    using var me = await RequestAsync(client, botUrl, "getMe", new { }, token);
                    if (me.RootElement.GetProperty("result").TryGetProperty("username", out var botName))
                        botMention = "@" + botName.GetString();
                    using var webhook = await RequestAsync(client, botUrl, "getWebhookInfo", new { }, token);
                    if (!string.IsNullOrEmpty(webhook.RootElement.GetProperty("result").GetProperty("url").GetString()))
                        throw new TelegramApiException("此机器人已配置 Webhook，请先在原服务中关闭消息接收", true);
                    using var pending = await RequestAsync(client, botUrl, "getUpdates", new { offset = -1, timeout = 0, allowed_updates = new[] { "message" } }, token);
                    foreach (var update in pending.RootElement.GetProperty("result").EnumerateArray())
                        offset = Math.Max(offset, update.GetProperty("update_id").GetInt64() + 1);
                    initialized = true;
                    SetStatus(Status with { State = "connected", Error = null });
                }
                using var document = await RequestAsync(client, botUrl, "getUpdates", new { offset, timeout = 30, allowed_updates = new[] { "message" } }, token);
                if (token.IsCancellationRequested) return;
                SetStatus(Status with { State = "connected", Error = null });
                retrySeconds = 2;
                foreach (var update in document.RootElement.GetProperty("result").EnumerateArray())
                {
                    var id = update.GetProperty("update_id").GetInt64();
                    if (id < offset) continue;
                    offset = id + 1;
                    if (!update.TryGetProperty("message", out var message) ||
                        !message.TryGetProperty("chat", out var chat) ||
                        chat.GetProperty("id").GetInt64() != long.Parse(settings.TelegramChatId.Trim(), CultureInfo.InvariantCulture)) continue;
                    var hasSender = message.TryGetProperty("from", out var from);
                    if (hasSender && from.TryGetProperty("is_bot", out var isBot) && isBot.ValueKind == JsonValueKind.True) continue;
                    var sender = hasSender
                        ? (from.TryGetProperty("username", out var username) ? username.GetString() : from.GetProperty("first_name").GetString())
                        : chat.TryGetProperty("title", out var title) ? title.GetString() : "未知发送者";
                    if (!message.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) continue;
                    var content = text.GetString() ?? "";
                    Receive(new(settings.TelegramChatId.Trim(), sender ?? "未知发送者", content)
                    {
                        CommandText = CommandBotService.StripLeadingBotMention(content, botMention),
                        ConversationType = chat.TryGetProperty("type", out var chatType) ? chatType.GetString() : null,
                        MessageId = message.TryGetProperty("message_id", out var messageId) ? messageId.GetRawText() : null,
                        ThreadId = message.TryGetProperty("message_thread_id", out var thread) && thread.TryGetInt64(out var threadId) ? threadId : null
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (TelegramApiException exception) when (exception.Permanent)
            {
                SetStatus(Status with { State = "failed", Error = exception.Message });
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or TelegramApiException or JsonException)
            {
                var message = exception is TelegramApiException api ? api.Message : "无法连接 Telegram，请检查网络、代理或 API 地址，正在重连";
                if (Status.State != "reconnecting") logger.LogWarning("Telegram 命令机器人连接中断，正在重连");
                SetStatus(Status with { State = "reconnecting", Error = message });
                var delay = exception is TelegramApiException rateLimit && rateLimit.RetryAfter is { } seconds
                    ? Math.Clamp(seconds, 1, 3600) : retrySeconds;
                try { await Task.Delay(TimeSpan.FromSeconds(delay), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                retrySeconds = Math.Min(retrySeconds * 2, 30);
            }
            catch (Exception)
            {
                SetStatus(Status with { State = "failed", Error = "Telegram 返回的数据无法处理，请检查 API 地址后重新连接" });
                return;
            }
        }
    }

    private static async Task<JsonDocument> RequestAsync(HttpClient client, string url, string method, object payload, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync($"{url}/{method}", payload, token);
        if (!response.IsSuccessStatusCode)
        {
            var failure = CreateApiError((int)response.StatusCode);
            if ((int)response.StatusCode == 429)
            {
                try
                {
                    using var details = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    if (details.RootElement.TryGetProperty("parameters", out var parameters) &&
                        parameters.TryGetProperty("retry_after", out var retry) && retry.TryGetInt32(out var seconds))
                        failure.RetryAfter = seconds;
                }
                catch (JsonException) { }
            }
            throw failure;
        }
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return document;
        var code = document.RootElement.TryGetProperty("error_code", out var error) ? error.GetInt32() : 502;
        document.Dispose();
        throw CreateApiError(code);
    }

    private static TelegramApiException CreateApiError(int code) => new(code switch
    {
        401 or 404 => "Telegram Token 无效或 API 地址错误，请修改配置后重新连接",
        403 => "Telegram 拒绝访问，请检查机器人权限",
        409 => "Telegram 消息接收被其他程序或 Webhook 占用，请关闭原接收服务后重新连接",
        429 => "Telegram 请求过于频繁，稍后自动重连",
        _ => $"Telegram 接口请求失败（{code}）"
    }, code is >= 400 and < 500 && code != 429);

    private sealed class TelegramApiException(string message, bool permanent) : Exception(message)
    {
        public bool Permanent { get; } = permanent;
        public int? RetryAfter { get; set; }
    }
}
