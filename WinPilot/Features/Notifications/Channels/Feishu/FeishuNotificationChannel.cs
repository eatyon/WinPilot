using System.Net.Http.Headers;
using System.Text.Json;
using WinPilot.Core;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal sealed class FeishuNotificationChannel(HttpClient httpClient) : IDisposable
{
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private TokenCache? cachedToken;
    internal async Task SendAsync(NotificationSettings settings, NotificationMessage message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.FeishuWebhookUrl)) throw new InvalidOperationException("飞书 Webhook 地址未配置");
        if (!string.IsNullOrWhiteSpace(message.ImagePath) &&
            (string.IsNullOrWhiteSpace(settings.FeishuAppId) || string.IsNullOrWhiteSpace(settings.FeishuAppSecret)))
            throw new InvalidOperationException("飞书图片通知需要配置 App ID 和 App Secret");
        var text = NotificationContent.FormatMessage(message);
        object body;
        if (!string.IsNullOrWhiteSpace(message.ImagePath))
        {
            var access = await GetTokenAsync(settings, token);
            var imageKey = await UploadImageAsync(message.ImagePath, access, token);
            body = new
            {
                msg_type = "post",
                content = new
                {
                    post = new
                    {
                        zh_cn = new
                        {
                            content = new[]
                            {
                                new object[] { new { tag = "text", text }, new { tag = "img", image_key = imageKey } }
                            }
                        }
                    }
                }
            };
        }
        else body = new { msg_type = "text", content = new { text } };
        using var response = await httpClient.PostAsJsonAsync(settings.FeishuWebhookUrl, body, cancellationToken: token);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetTokenAsync(NotificationSettings settings, CancellationToken token)
    {
        await tokenGate.WaitAsync(token);
        try
        {
            if (cachedToken is { } cached && cached.AppId == settings.FeishuAppId &&
                cached.Secret == settings.FeishuAppSecret && DateTime.UtcNow < cached.ExpiresAt)
                return cached.Token;
            cachedToken = null;
            using var response = await httpClient.PostAsJsonAsync("https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal",
                new { app_id = settings.FeishuAppId, app_secret = settings.FeishuAppSecret }, cancellationToken: token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            if (root.TryGetProperty("code", out _)) EnsureSuccess(root);
            var access = root.GetProperty("tenant_access_token").GetString();
            if (string.IsNullOrWhiteSpace(access)) throw new InvalidOperationException("飞书访问令牌无效");
            if (root.TryGetProperty("expire", out var expiry) && expiry.ValueKind == JsonValueKind.Number &&
                expiry.TryGetInt32(out var seconds) && seconds > 0)
                cachedToken = new(settings.FeishuAppId, settings.FeishuAppSecret, access,
                    DateTime.UtcNow.AddSeconds(seconds - Math.Min(60, seconds / 2)));
            return access;
        }
        finally { tokenGate.Release(); }
    }

    internal async Task ReplyAsync(NotificationSettings settings, CommandBots.CommandBotMessage message,
        string text, byte[]? image, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId)) throw new InvalidOperationException("飞书回复上下文不完整");
        var access = await GetTokenAsync(settings, token);
        var content = image is null
            ? JsonSerializer.Serialize(new { text })
            : JsonSerializer.Serialize(new { image_key = await UploadImageBytesAsync(image, access, token) });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://open.feishu.cn/open-apis/im/v1/messages/{Uri.EscapeDataString(message.MessageId)}/reply")
        {
            Content = JsonContent.Create(new { msg_type = image is null ? "text" : "image", content, reply_in_thread = false })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await httpClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        EnsureSuccess(result.RootElement);
    }

    private static void EnsureSuccess(JsonElement result)
    {
        if (!result.TryGetProperty("code", out var code) || !code.TryGetInt32(out var value) || value != 0)
            throw new InvalidOperationException("飞书接口返回失败");
    }

    private async Task<string> UploadImageAsync(string path, string accessToken, CancellationToken token)
    {
        var bytes = await NotificationContent.ReadPngBytesAsync(path, token);
        return await UploadImageBytesAsync(bytes, accessToken, token);
    }

    private async Task<string> UploadImageBytesAsync(byte[] bytes, string accessToken, CancellationToken token)
    {
        using var form = new MultipartFormDataContent();
        form.Add(NotificationContent.CreateImageContent(bytes), "image", NotificationContent.ImageFileName(bytes));
        form.Add(new StringContent("message"), "image_type");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/images") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("data").GetProperty("image_key").GetString()
            ?? throw new InvalidOperationException("飞书图片上传失败");
    }

    public void Dispose() => tokenGate.Dispose();
    private sealed record TokenCache(string AppId, string Secret, string Token, DateTime ExpiresAt);
}
