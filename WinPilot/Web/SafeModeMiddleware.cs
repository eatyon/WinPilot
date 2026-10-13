using WinPilot.Features.Security;

namespace WinPilot.Web;

public sealed class SafeModeMiddleware(RequestDelegate next)
{
    private static readonly PathString[] BlockedPathPrefixes = ["/api/files", "/api/terminal"];
    private static readonly PathString[] BlockedWritePathPrefixes = ["/api/settings"];
    private static readonly Dictionary<string, string[]> BlockedRoutesByMethod = new(StringComparer.OrdinalIgnoreCase)
    {
        [HttpMethods.Get] =
        [
            "/api/programs",
            "/api/programs/users"
        ],
        [HttpMethods.Post] =
        [
            "/api/desktop/keyboard",
            "/api/programs/usage",
            "/api/programs/close",
            "/api/programs/launch",
            "/api/programs/tags",
            "/api/tasks",
            "/api/notifications/test",
            "/api/notifications/qqbot/bind-user",
            "/api/notifications/qqbot/bind-group"
        ],
        [HttpMethods.Put] =
        [
            "/api/programs/tags/{name}",
            "/api/tasks/{name}",
            "/api/plugins/{id}/configuration",
            "/api/plugins/{id}/settings/display/status",
            "/api/plugins/{id}/settings/display/features",
            "/api/plugins/{id}/settings/display/configuration"
        ],
        [HttpMethods.Delete] =
        [
            "/api/programs/tags/{name}",
            "/api/tasks/{name}",
            "/api/plugins/{id}"
        ]
    };

    public async Task InvokeAsync(HttpContext context, SafeModeService safeMode)
    {
        if (!safeMode.Enabled || !IsBlocked(context.Request))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "safe_mode_restricted",
            message = "安全模式下不可执行此操作"
        });
    }

    private static bool IsBlocked(HttpRequest request)
    {
        var path = request.Path;
        foreach (var prefix in BlockedPathPrefixes)
            if (path.StartsWithSegments(prefix)) return true;

        var method = HttpMethods.IsHead(request.Method) ? HttpMethods.Get : request.Method;
        if (!HttpMethods.IsGet(method))
            foreach (var prefix in BlockedWritePathPrefixes)
                if (path.StartsWithSegments(prefix)) return true;

        var route = (request.HttpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        return route is not null && BlockedRoutesByMethod.TryGetValue(method, out var routes)
            && routes.Contains(route, StringComparer.OrdinalIgnoreCase);
    }
}
