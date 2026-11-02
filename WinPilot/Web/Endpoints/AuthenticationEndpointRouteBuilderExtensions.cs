using WinPilot.Core;
using WinPilot.Features.Security;

namespace WinPilot.Web;

public static class AuthenticationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/login", (
            LoginRequest request,
            HttpContext context,
            AuthStore auth,
            SessionManager sessions,
            LoginRateLimiter limiter,
            SecurityEventNotifier securityEvents,
            WebFacadeService facade) =>
        {
            if (!auth.IsConfigured)
            {
                return Results.NotFound();
            }
            var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var remaining = limiter.GetRemainingLockTime(source);
            if (remaining > TimeSpan.Zero)
            {
                context.Response.Headers.RetryAfter = Math.Ceiling(remaining.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
            if (!auth.Verify(request.Password, out var authenticationVersion))
            {
                securityEvents.Record(SecurityEventType.LoginFailed, source);
                if (limiter.Failure(source))
                {
                    securityEvents.Record(SecurityEventType.LoginSourceLocked, source);
                }
                return Results.Unauthorized();
            }
            // 创建会话前再次核对密码版本，拒绝与密码修改并发的旧登录结果。
            var session = sessions.Create(auth, authenticationVersion);
            if (session is null)
            {
                return Results.Unauthorized();
            }
            limiter.Success(source);
            if (facade.IsEnabled())
            {
                facade.ConsumeManagementEntry(context);
            }
            securityEvents.Record(SecurityEventType.LoginSucceeded, source);
            SessionCookie.Append(context, session);
            return Results.NoContent();
        });
        endpoints.MapPost("/_manage", (HttpContext context, AuthStore auth, WebFacadeService facade) =>
        {
            if (!auth.IsConfigured || !facade.IsEnabled() || !facade.GrantManagementEntry(context))
            {
                return Results.NotFound();
            }
            return Results.NoContent();
        });
        endpoints.MapPut("/api/settings/security/password", (
            PasswordChangeRequest request,
            AuthStore auth,
            SessionManager sessions,
            LoginRateLimiter limiter,
            SecurityEventNotifier securityEvents,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                AuthStore.ValidateNewPassword(request.NewPassword, request.Confirmation);
                var changed = auth.IsConfigured;
                if (changed)
                {
                    auth.ChangePassword(request.CurrentPassword, request.NewPassword);
                }
                else
                {
                    auth.SetInitialPassword(request.NewPassword);
                }
                // 新密码保存成功后清除锁定与旧会话，所有客户端需要重新验证。
                limiter.Reset();
                securityEvents.ObserveAuthConfiguration(true);
                sessions.RevokeAll();
                securityEvents.Record(changed ? SecurityEventType.PasswordChanged : SecurityEventType.PasswordSet);
                return Results.NoContent();
            }
            catch (InvalidOperationException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [exception.Message] });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                loggerFactory.CreateLogger("WinPilot.Security").LogError(exception, "密码保存失败");
                return Results.Problem(statusCode: 500, title: "密码保存失败", detail: "无法保存密码，请检查数据目录权限");
            }
        });
        endpoints.MapGet("/api/settings/security/password", (AuthStore auth) => Results.Ok(new { configured = auth.IsConfigured }));
        endpoints.MapGet(
            "/api/settings/security/notifications",
            (WinPilot.Core.PortableDataPaths paths) =>
                Results.Ok(WinPilot.Core.WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SecurityNotifications));
        endpoints.MapPut("/api/settings/security/notifications", (
            WinPilot.Core.SecurityNotificationSettings settings,
            WinPilot.Core.PortableDataPaths paths,
            SecurityEventNotifier securityEvents,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                settings = SecurityEventNotifier.Validate(settings);
                WinPilot.Core.SecurityNotificationSettings previous = new();
                WinPilot.Core.WinPilotSettings.Update(paths.ConfigurationFile, current =>
                {
                    previous = current.SecurityNotifications;
                    return current with { SecurityNotifications = settings };
                });
                // 配置变更事件按原设置通知，关闭通知的操作本身仍可被原接收方获知。
                securityEvents.Record(SecurityEventType.SecurityNotificationSettingsChanged, notificationSettings: previous);
                return Results.Ok(settings);
            }
            catch (InvalidOperationException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["notifications"] = [exception.Message] });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                loggerFactory.CreateLogger("WinPilot.Security").LogError(exception, "安全通知设置保存失败");
                return Results.Problem(statusCode: 500, title: "安全通知设置保存失败", detail: "无法保存安全通知设置，请检查数据目录权限");
            }
        });
        return endpoints;
    }

    private sealed record LoginRequest(string Password);

    private sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword, string Confirmation);
}
