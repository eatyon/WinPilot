using WinPilot.Core.Desktop;
using WinPilot.Features.Desktop;

namespace WinPilot.Features.Notifications.CommandBots.Commands;

internal sealed class DesktopScreenshotCommand(DesktopScreenshotService? screenshots)
{
    internal byte[] Capture()
    {
        var screenshot = (screenshots ?? throw new DesktopCaptureException("桌面截图服务不可用")).Capture();
        if (screenshot.Content.Length == 0) throw new DesktopCaptureException("桌面截图内容为空");
        return screenshot.Content;
    }
}
