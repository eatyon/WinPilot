using System.Drawing.Imaging;
using WinPilot.Core.Desktop;

namespace WinPilot.Windows;

public sealed class GdiDesktopScreenshotProvider : IDesktopScreenshotProvider
{
    public DesktopScreenshot Capture()
    {
        try
        {
            var desktopBounds = SystemInformation.VirtualScreen;
            if (desktopBounds.Width <= 0 || desktopBounds.Height <= 0)
            {
                throw new DesktopCaptureException("当前没有可捕获的桌面区域");
            }

            using var bitmap = new Bitmap(desktopBounds.Width, desktopBounds.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(desktopBounds.Location, Point.Empty, desktopBounds.Size, CopyPixelOperation.SourceCopy);
            }

            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Jpeg);
            return new DesktopScreenshot(DateTimeOffset.UtcNow, output.ToArray(), "image/jpeg");
        }
        catch (DesktopCaptureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DesktopCaptureException("无法获取当前桌面截图", exception);
        }
    }
}
