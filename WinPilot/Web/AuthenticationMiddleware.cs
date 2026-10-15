using WinPilot.Features.Security;

namespace WinPilot.Web;

public sealed class AuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuthStore auth, SessionManager sessions, SecurityEventNotifier securityEvents, WebFacadeService facade, SafeModeService safeMode, ILogger<AuthenticationMiddleware> logger)
    {
        var path = context.Request.Path;
        if (path == "/health") { await next(context); return; }
        var configured = auth.IsConfigured;
        Action? completeProtection = null;
        if (securityEvents.ObserveAuthConfiguration(configured, () => completeProtection = safeMode.EnableForAuthenticationFailure()))
        {
            try
            {
                try { sessions.RevokeAll(); }
                finally { completeProtection?.Invoke(); }
                logger.LogWarning(completeProtection is null
                    ? "密码配置缺失或损坏，安全模式保持开启；可通过本机托盘手动关闭"
                    : "密码配置缺失或损坏，已自动开启安全模式；可通过本机托盘手动关闭");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception, exception is IOException or UnauthorizedAccessException
                    || exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(error => error is IOException or UnauthorizedAccessException)
                    ? "密码配置缺失或损坏，安全模式已开启，但配置保存失败"
                    : "密码配置缺失或损坏，安全模式已开启，但保护收尾失败");
                await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "安全保护未能完成", detail: "安全模式已开启，请通过本机检查配置和程序日志").ExecuteAsync(context);
                return;
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
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/plugin-assets"))
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
