using System.Text.Json;
using WinPilot.Core;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal static class TelegramNotificationChannel
{
    internal static async Task SendAsync(NotificationSettings settings, NotificationMessage message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || string.IsNullOrWhiteSpace(settings.TelegramChatId))
            throw new InvalidOperationException("Telegram 配置不完整");
        var text = NotificationContent.FormatMessage(message);
        var image = string.IsNullOrWhiteSpace(message.ImagePath) ? null : await NotificationContent.ReadPngBytesAsync(message.ImagePath, token);
        await SendToAsync(settings, settings.TelegramChatId, text, image, null, token);
    }

    internal static async Task SendToAsync(NotificationSettings settings, string chatId, string text, byte[]? image,
        long? threadId, CancellationToken token)
    {
        using var client = TelegramTransport.CreateClient(settings, TimeSpan.FromSeconds(30));
        await SendToAsync(client, settings, chatId, text, image, threadId, token);
    }

    internal static async Task SendToAsync(HttpClient client, NotificationSettings settings, string chatId, string text,
        byte[]? image, long? threadId, CancellationToken token)
    {
        var baseUrl = TelegramTransport.GetBotUrl(settings);
        var textPayload = new Dictionary<string, object?> { ["chat_id"] = chatId, ["text"] = text, ["disable_web_page_preview"] = true };
        if (threadId.HasValue) textPayload["message_thread_id"] = threadId.Value;
        if (image is null)
        {
            await SendRequestAsync(client, $"{baseUrl}/sendMessage", textPayload, token, "text");
            return;
        }
        using var form = new MultipartFormDataContent
        {
            { new StringContent(chatId), "chat_id" },
            { NotificationContent.CreateImageContent(image), "photo", NotificationContent.ImageFileName(image) },
            { new StringContent(text.Length < 1024 ? text : ""), "caption" }
        };
        if (threadId.HasValue) form.Add(new StringContent(threadId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "message_thread_id");
        await SendRequestAsync(client, $"{baseUrl}/sendPhoto", form, token, "image");
        if (text.Length >= 1024) await SendRequestAsync(client, $"{baseUrl}/sendMessage", textPayload, token, "text");
    }

    private static async Task SendRequestAsync(HttpClient client, string url, object content, CancellationToken token, string type)
    {
        using var response = content is HttpContent httpContent
            ? await client.PostAsync(url, httpContent, token)
            : await client.PostAsJsonAsync(url, content, cancellationToken: token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"Telegram {type} API 返回失败");
    }
}
