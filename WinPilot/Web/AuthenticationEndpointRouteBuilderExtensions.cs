using WinPilot.Core;
using WinPilot.Features.Security;

namespace WinPilot.Web;

public static class AuthenticationEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/login", (LoginRequest request, HttpContext context, AuthStore auth, SessionManager sessions, LoginRateLimiter limiter, SecurityEventNotifier securityEvents, WebFacadeService facade) =>
        {
            if (!auth.IsConfigured) return Results.NotFound();
            var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var loopback = context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address);
            if (limiter.IsLocked(source)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            if (!auth.Verify(request.Password))
            {
                securityEvents.Record(SecurityEventType.LoginFailed, source, loopback, success: false);
                if (limiter.Failure(source)) securityEvents.Record(SecurityEventType.LoginSourceLocked, source, loopback, success: false, lockoutDuration: TimeSpan.FromMinutes(5));
                return Results.Unauthorized();
            }
            limiter.Success(source);
            if (facade.IsEnabled()) facade.ConsumeManagementEntry(context);
            securityEvents.Record(SecurityEventType.LoginSucceeded, source, loopback, success: true, allowNotification: !loopback);
            context.Response.Cookies.Append("WinPilot.Session", sessions.Create(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, Path = "/", MaxAge = TimeSpan.FromMinutes(30) });
            return Results.NoContent();
        });
        endpoints.MapPost("/_manage", (HttpContext context, AuthStore auth, WebFacadeService facade) =>
        {
            if (!auth.IsConfigured || !facade.IsEnabled() || !facade.GrantManagementEntry(context)) return Results.NotFound();
            return Results.NoContent();
        });
        endpoints.MapPut("/api/settings/security/password", (PasswordChangeRequest request, AuthStore auth, SessionManager sessions, SecurityEventNotifier securityEvents) =>
        {
            try
            {
                AuthStore.ValidateNewPassword(request.NewPassword, request.Confirmation);
                var changed = auth.IsConfigured;
                if (changed) auth.ChangePassword(request.CurrentPassword, request.NewPassword);
                else auth.SetInitialPassword(request.NewPassword);
                securityEvents.ObserveAuthConfiguration(true);
                sessions.RevokeAll();
                securityEvents.Record(changed ? SecurityEventType.PasswordChanged : SecurityEventType.PasswordSet);
                securityEvents.Record(SecurityEventType.SessionsRevoked);
                return Results.NoContent();
            }
            catch (InvalidOperationException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [exception.Message] }); }
        });
        endpoints.MapGet("/api/settings/security/password", (AuthStore auth) => Results.Ok(new { configured = auth.IsConfigured }));
        endpoints.MapGet("/api/settings/security/notifications", (WinPilot.Core.PortableDataPaths paths) => Results.Ok(WinPilot.Core.WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SecurityNotifications));
        endpoints.MapPut("/api/settings/security/notifications", (WinPilot.Core.SecurityNotificationSettings settings, WinPilot.Core.PortableDataPaths paths, SecurityEventNotifier securityEvents) =>
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
                securityEvents.Record(SecurityEventType.SecurityNotificationSettingsChanged, notificationSettings: previous);
                return Results.Ok(settings);
            }
            catch (InvalidOperationException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["notifications"] = [exception.Message] }); }
        });
        return endpoints;
    }
    private sealed record LoginRequest(string Password);
    private sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword, string Confirmation);
}
