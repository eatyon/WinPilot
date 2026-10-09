using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Core.Desktop;
using WinPilot.Features.Desktop;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class DesktopEndpointTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScreenshotResponsesCannotBeCached(bool fail)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDesktopScreenshotProvider>(new ScreenshotProvider(fail));
        builder.Services.AddSingleton<DesktopScreenshotService>();
        await using var app = builder.Build();
        app.MapDesktopEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/desktop/screenshot");

        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, response.StatusCode);
        if (!fail)
        {
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        }
    }

    private sealed class ScreenshotProvider(bool fail) : IDesktopScreenshotProvider
    {
        public DesktopScreenshot Capture() => fail
            ? throw new DesktopCaptureException("Capture failed")
            : new(DateTimeOffset.UtcNow, [1, 2, 3], "image/jpeg");
    }
}
