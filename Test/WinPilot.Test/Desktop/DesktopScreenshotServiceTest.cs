using WinPilot.Core.Desktop;
using WinPilot.Features.Desktop;

namespace WinPilot.Test;

public sealed class DesktopScreenshotServiceTest
{
    [Fact]
    public void Capture_ReturnsProviderScreenshot()
    {
        var expected = new DesktopScreenshot(DateTimeOffset.UtcNow, [1, 2, 3], "image/jpeg");
        var service = new DesktopScreenshotService(new FakeScreenshotProvider(expected));

        var actual = service.Capture();

        Assert.Same(expected, actual);
    }

    private sealed class FakeScreenshotProvider(DesktopScreenshot screenshot) : IDesktopScreenshotProvider
    {
        public DesktopScreenshot Capture() => screenshot;
    }
}
