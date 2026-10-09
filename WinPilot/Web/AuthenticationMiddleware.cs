using WinPilot.Features.Security;

namespace WinPilot.Web;

public sealed class AuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuthStore auth, SessionManager sessions, SecurityEventNotifier securityEvents, WebFacadeService facade, SafeModeService safeMode, ILogger<AuthenticationMiddleware> logger)
    {
        var path = context.Request.Path;
        if (path == "/health") { await next(context); return; }
        var configured = auth.IsConfigured;
        if (securityEvents.ObserveAuthConfiguration(configured))
        {
            sessions.RevokeAll();
            try
            {
                var wasEnabled = safeMode.Enabled;
                if (!wasEnabled) safeMode.SetEnabled(true);
                logger.LogWarning(wasEnabled
                    ? "密码配置缺失或损坏，安全模式保持开启；可通过本机托盘手动关闭"
                    : "密码配置缺失或损坏，已自动开启安全模式；可通过本机托盘手动关闭");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "密码配置缺失或损坏，自动开启安全模式失败");
            }
        }
        if (!configured)
        {
            await next(context);
            return;
        }

        var facadeEnabled = facade.IsEnabled();
        if (facadeEnabled && path == "/_manage") { await next(context); return; }
        if (sessions.TryValidate(context.Request.Cookies["WinPilot.Session"], out var sessionInvalidated))
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, sessionInvalidated);
            var originalCancellation = context.RequestAborted;
            context.RequestAborted = linkedCancellation.Token;
            try { await next(context); }
            finally { context.RequestAborted = originalCancellation; }
            return;
        }
        if (path.StartsWithSegments("/plugin-assets"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (facadeEnabled)
        {
            if (path == "/login")
            {
                if (facade.HasManagementEntry(context)) { await next(context); return; }
                if (AcceptsHtml(context)) context.Response.Redirect("/");
                else context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (path == "/" && HttpMethods.IsGet(context.Request.Method)) { await facade.ServeIndexAsync(context); return; }
            if (IsFacadeResource(path) && await facade.TryServeResourceAsync(context)) return;
            if (!path.StartsWithSegments("/api") && AcceptsHtml(context)) { context.Response.Redirect("/"); return; }
        }
        if (IsStaticAsset(path) || path == "/login") { await next(context); return; }
        if (AcceptsHtml(context)) { context.Response.Redirect("/login"); return; }
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private static bool IsStaticAsset(PathString path) => !path.StartsWithSegments("/api") && (path.StartsWithSegments("/assets") || Path.HasExtension(path));
    private static bool IsFacadeResource(PathString path) => IsStaticAsset(path) && !path.StartsWithSegments("/assets");
    private static bool AcceptsHtml(HttpContext context) => context.Request.Headers.Accept.Any(value => value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
}
