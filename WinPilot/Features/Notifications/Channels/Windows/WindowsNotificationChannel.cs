using System.Xml.Linq;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

/// <summary>
/// 将统一正文适配为 Windows 原生通知，首行作为首个文本块，其余内容作为第二个文本块。
/// </summary>
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
        // 最多拆成两段，保留正文后续换行；使用 XML 节点自动转义用户文本。
        var binding = new XElement(
            "binding",
            new XAttribute("template", "ToastGeneric"),
            message.Content
                .Split(["\r\n", "\n", "\r"], 2, StringSplitOptions.None)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => new XElement("text", text)));
        if (!string.IsNullOrWhiteSpace(message.ImagePath))
        {
            if (!File.Exists(message.ImagePath))
            {
                throw new FileNotFoundException("通知图片不存在", message.ImagePath);
            }
            binding.Add(new XElement(
                "image",
                new XAttribute("placement", "hero"),
                new XAttribute("src", new Uri(Path.GetFullPath(message.ImagePath)).AbsoluteUri)));
        }
        return new XElement("toast", new XElement("visual", binding)).ToString(SaveOptions.DisableFormatting);
    }
}
