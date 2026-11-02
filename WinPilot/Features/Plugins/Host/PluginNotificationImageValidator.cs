using WinPilot.Features.Notifications;
using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

internal static class PluginNotificationImageValidator
{
    internal static void ValidateImageSize(PluginImageContent? image)
    {
        if (image is not null
            && (image.Content.Length == 0 || image.Content.Length > NotificationDelivery.MaxImageSizeBytes))
        {
            throw new InvalidOperationException("通知图片大小必须在 1 字节到 10 MB 之间");
        }
    }
}
