using System.Net.Http.Headers;
using System.Text.Json;
using WinPilot.Core;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal sealed class OneBotNotificationChannel(HttpClient httpClient)
{
    internal async Task SendAsync(NotificationSettings settings, NotificationMessage message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.OneBotEndpoint)) throw new InvalidOperationException("OneBot 地址未配置");
        var targets = SelectTargets(settings);
        var endpoint = settings.OneBotEndpoint.TrimEnd('/');
        var url = endpoint.EndsWith("/send_msg", StringComparison.OrdinalIgnoreCase) ? endpoint : endpoint + "/send_msg";
        var text = NotificationContent.FormatMessage(message);
        var image = string.IsNullOrWhiteSpace(message.ImagePath) ? null : await NotificationContent.ReadPngBytesAsync(message.ImagePath, token);
        var segments = CreateSegments(text, image);
        foreach (var target in targets)
        {
            var payload = new Dictionary<string, object?>
            {
                ["message_type"] = target.Item1,
                ["message"] = segments,
                [target.Item1 == "private" ? "user_id" : "group_id"] = target.Item2
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            if (!string.IsNullOrWhiteSpace(settings.OneBotToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OneBotToken);
            using var response = await httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("status", out var status) ||
                !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OneBot 返回失败");
        }
    }

    internal static IReadOnlyList<object> CreateSegments(string text, byte[]? image)
    {
        var segments = new List<object>();
        if (!string.IsNullOrWhiteSpace(text)) segments.Add(new { type = "text", data = new { text } });
        if (image is not null) segments.Add(new { type = "image", data = new { file = "base64://" + Convert.ToBase64String(image) } });
        return segments;
    }

    internal static (string Type, string Id)[] SelectTargets(NotificationSettings settings)
    {
        var mode = settings.OneBotSendTarget;
        var candidates = mode switch
        {
            "auto" => new[] { ("private", settings.OneBotUserId), ("group", settings.OneBotGroupId) },
            "private" => new[] { ("private", settings.OneBotUserId) },
            "group" => new[] { ("group", settings.OneBotGroupId) },
            _ => throw new InvalidOperationException($"OneBot 发送目标无效")
        };
        var targets = candidates.Where(item => !string.IsNullOrWhiteSpace(item.Item2)).ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException(mode switch
            {
                "private" => $"OneBot 未配置私聊发送对象",
                "group" => $"OneBot 未配置群聊发送对象",
                _ => $"OneBot 未配置发送对象"
            });
        return targets;
    }
}
