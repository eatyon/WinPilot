using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using WinPilot.Core;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal sealed class QQBotNotificationChannel(HttpClient httpClient) : IDisposable
{
    private TokenCache? cachedToken;
    private readonly SemaphoreSlim tokenGate = new(1, 1);

    internal async Task SendAsync(NotificationSettings settings, NotificationMessage message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.QQBotAppId) || string.IsNullOrWhiteSpace(settings.QQBotClientSecret))
            throw new InvalidOperationException("QQBot 配置不完整");
        var targets = SelectTargets(settings);
        var access = await GetAccessTokenAsync(settings, token);
        foreach (var target in targets)
        {
            var targetType = target.Type == "private" ? "users" : "groups";
            var baseUrl = $"https://api.sgroup.qq.com/v2/{targetType}/{Uri.EscapeDataString(target.Id)}";
            var textSent = false;
            if (!string.IsNullOrWhiteSpace(message.Content) ||
                string.IsNullOrWhiteSpace(message.ImagePath) && !string.IsNullOrWhiteSpace(message.Title))
            {
                await SendRequestAsync(baseUrl + "/messages",
                    CreateTextPayload(settings, NotificationContent.FormatMessage(message)), access, token);
                textSent = true;
            }
            if (!string.IsNullOrWhiteSpace(message.ImagePath))
            {
                try
                {
                    var info = await UploadImageAsync(baseUrl, message.ImagePath, access, token);
                    await SendRequestAsync(baseUrl + "/messages", new { msg_type = 7, media = new { file_info = info } }, access, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new InvalidOperationException(textSent ? "QQBot 文字已发送，但图片发送失败" : "QQBot 图片发送失败", exception);
                }
            }
        }
    }

    private async Task<string> GetAccessTokenAsync(NotificationSettings settings, CancellationToken token)
    {
        await tokenGate.WaitAsync(token);
        try
        {
            if (cachedToken is { } cached && cached.AppId == settings.QQBotAppId &&
                cached.ClientSecret == settings.QQBotClientSecret && DateTime.UtcNow < cached.ExpiresAt)
                return cached.AccessToken;
            cachedToken = null;
            using var response = await httpClient.PostAsJsonAsync("https://bots.qq.com/app/getAppAccessToken",
                new { appId = settings.QQBotAppId, clientSecret = settings.QQBotClientSecret }, cancellationToken: token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var accessToken = document.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("QQBot 访问令牌无效");
            var expires = 3600;
            if (document.RootElement.TryGetProperty("expires_in", out var expiry))
                expires = expiry.ValueKind == JsonValueKind.Number ? expiry.GetInt32()
                    : int.TryParse(expiry.GetString(), out var seconds) ? seconds : expires;
            var cacheSeconds = Math.Max(0, expires - Math.Min(60, expires / 2));
            cachedToken = new(settings.QQBotAppId, settings.QQBotClientSecret, accessToken, DateTime.UtcNow.AddSeconds(cacheSeconds));
            return accessToken;
        }
        finally { tokenGate.Release(); }
    }

    internal async Task ReplyAsync(NotificationSettings settings, CommandBots.CommandBotMessage message,
        string text, byte[]? image, int sequence, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId) || message.ConversationType is not ("private" or "group"))
            throw new InvalidOperationException("QQBot 回复上下文不完整");
        var access = await GetAccessTokenAsync(settings, token);
        var type = message.ConversationType == "private" ? "users" : "groups";
        var url = $"https://api.sgroup.qq.com/v2/{type}/{Uri.EscapeDataString(message.ChatId)}";
        object payload;
        if (image is null)
            payload = CreateTextPayload(settings, text, message.MessageId, sequence);
        else
        {
            var info = await UploadImageBytesAsync(url, NotificationContent.ToJpegBytes(image), access, token);
            payload = new { msg_type = 7, content = "", media = new { file_info = info }, msg_id = message.MessageId, msg_seq = sequence };
        }
        await SendRequestAsync(url + "/messages", payload, access, token);
    }

    internal static void ValidateMessageFormat(NotificationSettings settings)
    {
        NotificationConfigurationValidator.ValidateQQBotMessageFormat(settings.QQBotMessageFormat);
    }

    private static Dictionary<string, object> CreateTextPayload(NotificationSettings settings, string text,
        string? messageId = null, int sequence = 1)
    {
        ValidateMessageFormat(settings);
        var markdown = settings.QQBotMessageFormat == "markdown";
        var payload = new Dictionary<string, object>
        {
            ["msg_type"] = markdown ? 2 : 0,
            [markdown ? "markdown" : "content"] = markdown ? new { content = text } : text
        };
        if (messageId is not null)
        {
            payload["msg_id"] = messageId;
            payload["msg_seq"] = sequence;
        }
        return payload;
    }

    private async Task SendRequestAsync(string url, object payload, string access, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", access);
        using var response = await httpClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> UploadImageAsync(string baseUrl, string path, string access, CancellationToken token)
    {
        var bytes = await NotificationContent.ReadJpegBytesAsync(path, token) ?? throw new InvalidOperationException("QQBot 图片不存在");
        return await UploadImageBytesAsync(baseUrl, bytes, access, token);
    }

    private async Task<string> UploadImageBytesAsync(string baseUrl, byte[] bytes, string access, CancellationToken token)
    {
        var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var md5Part = Convert.ToHexString(MD5.HashData(bytes.AsSpan(0, Math.Min(bytes.Length, 10002432)))).ToLowerInvariant();
        using var prepare = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/upload_prepare")
        {
            Content = JsonContent.Create(new
            {
                file_type = 1, file_size = bytes.Length.ToString(), file_name = "screenshot.jpg", md5, sha1, md5_10m = md5Part
            })
        };
        prepare.Headers.Authorization = new AuthenticationHeaderValue("QQBot", access);
        using var prepared = await httpClient.SendAsync(prepare, token);
        prepared.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await prepared.Content.ReadAsStringAsync(token));
        var root = document.RootElement;
        var uploadId = root.GetProperty("upload_id").GetString()!;
        var blockSize = int.Parse(root.GetProperty("block_size").GetString()!);
        foreach (var part in root.GetProperty("parts").EnumerateArray())
        {
            var index = part.GetProperty("index").GetInt32();
            var start = (index - 1) * blockSize;
            var chunk = bytes[start..Math.Min(start + blockSize, bytes.Length)];
            using var put = new ByteArrayContent(chunk);
            using var putResponse = await httpClient.PutAsync(part.GetProperty("presigned_url").GetString()!, put, token);
            putResponse.EnsureSuccessStatusCode();
            using var finish = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/upload_part_finish")
            {
                Content = JsonContent.Create(new
                {
                    upload_id = uploadId, part_index = index, block_size = chunk.Length.ToString(),
                    md5 = Convert.ToHexString(MD5.HashData(chunk)).ToLowerInvariant()
                })
            };
            finish.Headers.Authorization = new AuthenticationHeaderValue("QQBot", access);
            using var finishResponse = await httpClient.SendAsync(finish, token);
            finishResponse.EnsureSuccessStatusCode();
        }
        using var merge = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/files")
        {
            Content = JsonContent.Create(new { file_type = 1, upload_id = uploadId })
        };
        merge.Headers.Authorization = new AuthenticationHeaderValue("QQBot", access);
        using var merged = await httpClient.SendAsync(merge, token);
        merged.EnsureSuccessStatusCode();
        using var mergedDocument = JsonDocument.Parse(await merged.Content.ReadAsStringAsync(token));
        return mergedDocument.RootElement.GetProperty("file_info").GetString()!;
    }

    public void Dispose() => tokenGate.Dispose();

    private sealed record TokenCache(string AppId, string ClientSecret, string AccessToken, DateTime ExpiresAt);

    internal static (string Type, string Id)[] SelectTargets(NotificationSettings settings)
    {
        var mode = settings.QQBotSendTarget;
        NotificationConfigurationValidator.ValidateSendTarget(mode, "QQBot");
        var candidates = mode switch
        {
            "auto" => new[] { ("private", settings.QQBotOpenId), ("group", settings.QQBotGroupOpenId) },
            "private" => new[] { ("private", settings.QQBotOpenId) },
            "group" => new[] { ("group", settings.QQBotGroupOpenId) },
            _ => throw new InvalidOperationException($"QQBot 发送目标无效")
        };
        var targets = candidates.Where(item => !string.IsNullOrWhiteSpace(item.Item2)).ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException(mode switch
            {
                "private" => $"QQBot 未配置私聊发送对象",
                "group" => $"QQBot 未配置群聊发送对象",
                _ => $"QQBot 未配置发送对象"
            });
        return targets;
    }
}
