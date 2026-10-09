using WinPilot.Core;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal sealed class WebhookNotificationChannel(HttpClient httpClient)
{
    internal async Task SendAsync(NotificationSettings settings, NotificationMessage message, CancellationToken token)
    {
        if (!Uri.TryCreate(settings.WebhookUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("Webhook 地址无效");
        var payload = new Dictionary<string, object?>
        {
            ["send_to"] = settings.WebhookSendTo,
            ["event"] = message.Event,
            ["result"] = message.Result,
            ["timestamp"] = message.Timestamp ?? DateTimeOffset.Now.ToString("O"),
            ["screenshot"] = await NotificationContent.ReadJpegBase64Async(message.ImagePath, token),
            ["message"] = NotificationContent.FormatMessage(message),
            ["data"] = message.Data
        };
        using var response = await httpClient.PostAsJsonAsync(settings.WebhookUrl, payload, cancellationToken: token);
        response.EnsureSuccessStatusCode();
    }
}
