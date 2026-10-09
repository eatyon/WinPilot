using System.Text.Json;
using WinPilot.Features.Security;

namespace WinPilot.Web;

public static class SafeModeEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapSafeModeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/security/safe-mode", (SafeModeService safeMode) => Results.Ok(new { enabled = safeMode.Enabled }));
        endpoints.MapGet("/api/security/safe-mode/events", async (HttpContext context, SafeModeService safeMode) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            try
            {
                await foreach (var enabled in safeMode.WatchAsync(context.RequestAborted))
                {
                    await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { enabled })}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        return endpoints;
    }
}
