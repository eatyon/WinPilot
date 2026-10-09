using System.Security.Cryptography;
using WinPilot.Core;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal sealed class WorkWeixinNotificationChannel(HttpClient httpClient)
{
    internal async Task SendAsync(NotificationSettings settings, NotificationMessage message, CancellationToken token)
    {
        var url = settings.WorkWeixinWebhookUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw new InvalidOperationException("企业微信 Webhook 地址无效");
        var jpeg = await NotificationContent.ReadJpegBytesAsync(message.ImagePath, token);
        if (jpeg is not null)
        {
            using var imageResponse = await httpClient.PostAsJsonAsync(url, new
            {
                msgtype = "image",
                image = new
                {
                    base64 = Convert.ToBase64String(jpeg),
                    md5 = Convert.ToHexString(MD5.HashData(jpeg)).ToLowerInvariant()
                }
            }, cancellationToken: token);
            imageResponse.EnsureSuccessStatusCode();
            await Task.Delay(1000, token);
        }
        var text = NotificationContent.FormatMessage(message);
        if (!string.IsNullOrWhiteSpace(text))
        {
            using var textResponse = await httpClient.PostAsJsonAsync(url,
                new { msgtype = "text", text = new { content = text } }, cancellationToken: token);
            textResponse.EnsureSuccessStatusCode();
        }
    }
}
