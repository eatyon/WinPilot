using WinPilot.Core.Desktop;

namespace WinPilot.Features.Desktop;

public sealed class DesktopScreenshotService(IDesktopScreenshotProvider screenshotProvider)
{
    public DesktopScreenshot Capture() => screenshotProvider.Capture();
}
