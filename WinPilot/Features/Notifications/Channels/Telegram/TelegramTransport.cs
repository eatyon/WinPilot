using System.Net;
using WinPilot.Core;

namespace WinPilot.Features.Notifications;

internal static class TelegramTransport
{
    internal static string GetBotUrl(NotificationSettings settings)
    {
        var url = string.IsNullOrWhiteSpace(settings.TelegramApiBaseUrl)
            ? "https://api.telegram.org/bot" : settings.TelegramApiBaseUrl.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!url.EndsWith("/bot", StringComparison.OrdinalIgnoreCase)) url += "/bot";
        return url + settings.TelegramBotToken.Trim();
    }

    internal static HttpClient CreateClient(NotificationSettings settings, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        if (settings.TelegramProxyEnabled && !string.IsNullOrWhiteSpace(settings.TelegramProxyUrl))
        {
            handler.Proxy = new WebProxy(settings.TelegramProxyUrl.Trim());
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = timeout };
    }
}
