using WinPilot.Features.Security;

namespace WinPilot.Web;

public sealed class SafeModeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SafeModeService safeMode)
    {
        if (!safeMode.Enabled || IsAllowed(context.Request))
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

    private static bool IsAllowed(HttpRequest request)
    {
        var path = request.Path;
        var method = request.Method;
        if (!path.StartsWithSegments("/api")) return true;
        if (HttpMethods.IsGet(method) && path == "/api/security/safe-mode") return true;
        if (HttpMethods.IsGet(method) && path == "/api/security/safe-mode/events") return true;
        if (HttpMethods.IsGet(method) && path == "/api/about") return true;
        if (path.StartsWithSegments("/api/update")) return !(HttpMethods.IsPut(method) && path == "/api/update/settings");
        if (HttpMethods.IsGet(method) && path == "/api/system/status") return true;
        if (HttpMethods.IsGet(method) && path == "/api/desktop/screenshot") return true;
        if (HttpMethods.IsGet(method) && path.StartsWithSegments("/api/logs")) return true;
        if (HttpMethods.IsGet(method) && path.StartsWithSegments("/api/settings")) return true;
        if (HttpMethods.IsGet(method) && (path == "/api/security/password" || path == "/api/security/notifications")) return true;
        if (HttpMethods.IsGet(method) && path == "/api/programs/tags") return true;
        if (HttpMethods.IsPost(method) && path.StartsWithSegments("/api/programs/tags") && path.Value!.EndsWith("/launch", StringComparison.OrdinalIgnoreCase)) return true;
        if (HttpMethods.IsGet(method) && (path == "/api/tasks" || path == "/api/task-variables")) return true;
        if (HttpMethods.IsGet(method) && path.StartsWithSegments("/api/plugin-task-extensions")) return true;
        if ((HttpMethods.IsPost(method) || HttpMethods.IsPatch(method)) && path.StartsWithSegments("/api/tasks") &&
            (path.Value!.EndsWith("/run", StringComparison.OrdinalIgnoreCase) || path.Value.EndsWith("/enabled", StringComparison.OrdinalIgnoreCase))) return true;
        if (path.StartsWithSegments("/api/plugins") || path.StartsWithSegments("/api/plugin-page-sessions"))
            return IsAllowedPluginRequest(request);
        return false;
    }

    private static bool IsAllowedPluginRequest(HttpRequest request)
    {
        var path = request.Path.Value!;
        if (HttpMethods.IsGet(request.Method)) return true;
        if (HttpMethods.IsDelete(request.Method)) return path.StartsWith("/api/plugin-", StringComparison.OrdinalIgnoreCase);
        if (!HttpMethods.IsPost(request.Method)) return false;
        return path.Equals("/api/plugins/reload", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/enable", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/disable", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/features/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/plugin-page-sessions/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/log", StringComparison.OrdinalIgnoreCase);
    }
}
