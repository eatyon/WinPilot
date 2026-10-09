using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal static class WindowsNotificationChannel
{
    internal static Task SendAsync(NotificationMessage message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var builder = new ToastContentBuilder();
        foreach (var text in new[] { message.Title, message.Content }.Where(text => !string.IsNullOrWhiteSpace(text)))
            builder.AddText(text);
        if (!string.IsNullOrWhiteSpace(message.ImagePath))
        {
            if (!File.Exists(message.ImagePath)) throw new FileNotFoundException("通知图片不存在", message.ImagePath);
            builder.AddHeroImage(new Uri(Path.GetFullPath(message.ImagePath)));
        }
        var toast = new ToastNotification(builder.GetToastContent().GetXml())
        {
            Group = message.Event,
            ExpirationTime = DateTime.Now.AddHours(12)
        };
        ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
        return Task.CompletedTask;
    }
}
