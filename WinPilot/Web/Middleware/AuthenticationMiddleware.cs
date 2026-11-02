using WinPilot.Features.Security;

namespace WinPilot.Web;

/// <summary>
/// 处理认证配置失效保护、会话校验，以及管理页面、API 和伪装页面各自的未认证响应。
/// </summary>
public sealed class AuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        AuthStore auth,
        SessionManager sessions,
        LoginRateLimiter limiter,
        SecurityEventNotifier securityEvents,
        WebFacadeService facade,
        SafeModeService safeMode,
        ILogger<AuthenticationMiddleware> logger)
    {
        var path = context.Request.Path;
        if (path == "/health")
        {
            await next(context);
            return;
        }
        var configured = auth.IsConfigured;
        Action? completeProtection = null;
        // 先开启保护，再撤销会话并完成收尾；不能在密码文件失效后继续沿用旧登录状态。
        if (securityEvents.ObserveAuthConfiguration(configured, () => completeProtection = safeMode.EnableForAuthenticationFailure()))
        {
            limiter.Reset();
            try
            {
                try
                {
                    sessions.RevokeAll();
                }
                finally
                {
                    completeProtection?.Invoke();
                }
                logger.LogWarning(completeProtection is null
                    ? "密码配置缺失或损坏，安全模式保持开启；可通过本机托盘手动关闭"
                    : "密码配置缺失或损坏，已自动开启安全模式；可通过本机托盘手动关闭");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    exception is IOException or UnauthorizedAccessException
                    || exception is AggregateException aggregate
                    && aggregate.Flatten().InnerExceptions.All(error => error is IOException or UnauthorizedAccessException)
                        ? "密码配置缺失或损坏，安全模式已开启，但配置保存失败"
                        : "密码配置缺失或损坏，安全模式已开启，但保护收尾失败");
                await Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "安全保护未能完成",
                    detail: "安全模式已开启，请通过本机检查配置和程序日志").ExecuteAsync(context);
                return;
            }
        }
        if (!configured)
        {
            if (path == "/login"
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                context.Response.Redirect("/home");
                return;
            }
            await next(context);
            return;
        }

        var facadeEnabled = facade.IsEnabled();
        if (facadeEnabled && path == "/_manage")
        {
            await next(context);
            return;
        }
        var session = context.Request.Cookies[SessionCookie.Name];
        if (sessions.TryValidate(session, out var sessionInvalidated))
        {
            // 在发送响应头时续期；登录端点写入的新会话 Cookie 优先。
            context.Response.OnStarting(() =>
            {
                var cookieWritten = context.Response.Headers.SetCookie.Any(value =>
                    value?.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal) == true);
                if (!sessionInvalidated.IsCancellationRequested && !cookieWritten)
                {
                    SessionCookie.Append(context, session!);
                }
                return Task.CompletedTask;
            });
            // 将会话失效与客户端断开合并，让 SSE、WebSocket 等请求同步结束。
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, sessionInvalidated);
            var originalCancellation = context.RequestAborted;
            context.RequestAborted = linkedCancellation.Token;
            try
            {
                await next(context);
            }
            finally
            {
                context.RequestAborted = originalCancellation;
            }
            return;
        }
        // 数据请求返回 401，页面导航才重定向，避免客户端把登录页 HTML 当成接口结果。
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/plugin-assets"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (facadeEnabled)
        {
            if (path == "/login")
            {
                if (facade.HasManagementEntry(context))
                {
                    await next(context);
                    return;
                }
                if (AcceptsHtml(context))
                {
                    context.Response.Redirect("/");
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                }
                return;
            }
            if (path == "/" && HttpMethods.IsGet(context.Request.Method))
            {
                await facade.ServeIndexAsync(context);
                return;
            }
            if (IsFacadeResource(path) && await facade.TryServeResourceAsync(context))
            {
                return;
            }
            if (!path.StartsWithSegments("/api") && AcceptsHtml(context))
            {
                context.Response.Redirect("/");
                return;
            }
        }
        if (IsStaticAsset(path) || path == "/login")
        {
            await next(context);
            return;
        }
        if (AcceptsHtml(context))
        {
            context.Response.Redirect("/login");
            return;
        }
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private static bool IsStaticAsset(PathString path) =>
        !path.StartsWithSegments("/api") && (path.StartsWithSegments("/assets") || Path.HasExtension(path));

    private static bool IsFacadeResource(PathString path) => IsStaticAsset(path) && !path.StartsWithSegments("/assets");

    private static bool AcceptsHtml(HttpContext context) =>
        context.Request.Headers.Accept.Any(value => value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
}
