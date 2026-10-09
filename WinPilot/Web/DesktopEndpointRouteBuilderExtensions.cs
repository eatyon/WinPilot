using WinPilot.Core.Desktop;
using WinPilot.Features.Desktop;

namespace WinPilot.Web;

public static class DesktopEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDesktopEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/desktop/screenshot", (HttpContext context, DesktopScreenshotService screenshotService, ILoggerFactory loggerFactory) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var logger = loggerFactory.CreateLogger(typeof(DesktopEndpointRouteBuilderExtensions).FullName!);

            try
            {
                var screenshot = screenshotService.Capture();
                return Results.File(screenshot.Content, screenshot.ContentType, lastModified: screenshot.CapturedAt);
            }
            catch (DesktopCaptureException exception)
            {
                logger.LogError(exception, "桌面截图失败");
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "桌面截图失败", detail: exception.Message);
            }
        });

        return endpoints;
    }
}
