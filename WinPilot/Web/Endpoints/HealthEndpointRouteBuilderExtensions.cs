using WinPilot.Core;

namespace WinPilot.Web;

public static class HealthEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapHealthEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        endpoints.MapGet("/api/about", () => Results.Ok(new { version = WinPilotVersion.DisplayVersion }));

        return endpoints;
    }
}
