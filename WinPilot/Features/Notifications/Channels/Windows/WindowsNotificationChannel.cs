using System.Xml.Linq;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal static class WindowsNotificationChannel
{
    internal static Task SendAsync(NotificationMessage message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var xml = CreateContent(message);
        NativeWindowsNotification.Send(xml, message.Event, token);
        return Task.CompletedTask;
    }

    internal static string CreateContent(NotificationMessage message)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new[] { message.Title, message.Content }
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => new XElement("text", text)));
        if (!string.IsNullOrWhiteSpace(message.ImagePath))
        {
            if (!File.Exists(message.ImagePath)) throw new FileNotFoundException("通知图片不存在", message.ImagePath);
            binding.Add(new XElement("image", new XAttribute("placement", "hero"),
                new XAttribute("src", new Uri(Path.GetFullPath(message.ImagePath)).AbsoluteUri)));
        }
        return new XElement("toast", new XElement("visual", binding)).ToString(SaveOptions.DisableFormatting);
    }
}
