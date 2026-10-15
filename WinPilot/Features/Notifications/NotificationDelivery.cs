using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal static class NotificationDelivery
{
    internal const int MaxImageSizeBytes = 10 * 1024 * 1024;

    internal static async Task SendAsync(INotificationService notifications, NotificationMessage message,
        CancellationToken token, string? channel = null, bool ignoreChannelEnabled = false,
        byte[]? imageContent = null, string? imageContentType = null, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(message.Content) && string.IsNullOrWhiteSpace(message.ImagePath) && imageContent is null)
            throw new InvalidOperationException("请输入通知内容或通知图片");
        var temporaryImagePath = imageContent is null ? null : await WriteTemporaryImageAsync(imageContent, imageContentType!, token, logger);
        try
        {
            var deliveryMessage = temporaryImagePath is null ? message : message with { ImagePath = temporaryImagePath };
            ValidateResults(await notifications.SendAsync(deliveryMessage, token, channel, ignoreChannelEnabled));
            if (temporaryImagePath is not null)
                await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        finally
        {
            if (temporaryImagePath is not null)
                DeleteTemporaryImage(temporaryImagePath, logger);
        }
    }

    internal static async Task<string> WriteTemporaryImageAsync(byte[] content, string contentType, CancellationToken token, ILogger? logger = null,
        Func<string, byte[], CancellationToken, Task>? write = null)
    {
        if (content.Length == 0)
            throw new InvalidOperationException("通知图片不能为空");
        if (content.Length > MaxImageSizeBytes)
            throw new InvalidOperationException("通知图片不能超过 10 MB");
        var extension = contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            _ => throw new InvalidOperationException("通知图片仅支持 PNG 或 JPEG")
        };
        var path = Path.Combine(Path.GetTempPath(), $"WinPilot-notification-{Guid.NewGuid():N}{extension}");
        try
        {
            await (write is null ? File.WriteAllBytesAsync(path, content, token) : write(path, content, token));
            return path;
        }
        catch
        {
            DeleteTemporaryImage(path, logger);
            throw;
        }
    }

    internal static void ValidateResults(IReadOnlyList<NotificationChannelResult> results)
    {
        if (results.Count == 0)
            throw new InvalidOperationException("没有可用的通知渠道");
        var failed = results.Where(result => !result.Succeeded).Select(result => result.Channel).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (failed.Length > 0)
            throw new InvalidOperationException($"通知发送失败：{string.Join('、', failed)}");
    }

    internal static void DeleteTemporaryImage(string path, ILogger? logger)
    {
        try { File.Delete(path); }
        catch (Exception exception) { logger?.LogWarning(exception, "临时通知图片清理失败，文件：{Path}", path); }
    }
}
