using WinPilot.Core;
using WinPilot.Windows;
using WinPilot.Features.Security;

namespace WinPilot.Web;

public static class SettingsEndpointRouteBuilderExtensions
{
    private static readonly object startupSettingsGate = new();
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints, string configurationFile, StartupTaskService startupTasks)
    {
        endpoints.MapGet("/api/settings/unauthenticated-web-page", () =>
            Results.Ok(new { enabled = WinPilotSettings.LoadAndInitialize(configurationFile).UnauthenticatedWebPageEnabled }));
        endpoints.MapPut("/api/settings/unauthenticated-web-page", (UnauthenticatedWebPageRequest request, WebFacadeService facade, ILoggerFactory loggerFactory) =>
        {
            try
            {
                if (request.Enabled) facade.EnsureDefaultPage();
                var settings = WinPilotSettings.Update(configurationFile, current => current with { UnauthenticatedWebPageEnabled = request.Enabled });
                return Results.Ok(new { enabled = settings.UnauthenticatedWebPageEnabled });
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger(typeof(SettingsEndpointRouteBuilderExtensions).FullName!).LogError(exception, "更新自定义未认证页面设置失败");
                return Results.Problem(statusCode: 500, title: "自定义未认证页面设置失败", detail: "无法保存设置或创建默认页面");
            }
        });

        endpoints.MapGet("/api/settings", () =>
        {
            var settings = WinPilotSettings.LoadAndInitialize(configurationFile);
            return Results.Ok(ToResponse(settings));
        });

        endpoints.MapPatch("/api/settings/lan-access", (LanAccessRequest request, SecurityEventNotifier securityEvents, ILoggerFactory loggerFactory) =>
        {
            try
            {
                var changed = false;
                var settings = WinPilotSettings.Update(configurationFile, current =>
                {
                    changed = current.LanAccessEnabled != request.Enabled;
                    return current with { LanAccessEnabled = request.Enabled };
                });
                if (changed) securityEvents.Record(SecurityEventType.LanAccessChanged, enabled: request.Enabled);
                loggerFactory.CreateLogger(typeof(SettingsEndpointRouteBuilderExtensions).FullName!).LogInformation("局域网访问开关已更新");
                return Results.Ok(ToResponse(settings));
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger(typeof(SettingsEndpointRouteBuilderExtensions).FullName!).LogError(exception, "更新局域网访问开关失败");
                return Results.Problem(statusCode: 500, title: "局域网访问设置失败", detail: "无法保存局域网访问设置");
            }
        });

        endpoints.MapPatch("/api/settings/port", (PortRequest request, ILoggerFactory loggerFactory) =>
        {
            if (request.Port is < 1 or > 65535) return Results.ValidationProblem(new Dictionary<string, string[]> { ["port"] = ["端口必须在 1 到 65535 之间"] });
            try
            {
                var settings = WinPilotSettings.Update(configurationFile, current => current with { Port = request.Port });
                return Results.Ok(ToResponse(settings));
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger(typeof(SettingsEndpointRouteBuilderExtensions).FullName!).LogError(exception, "更新端口失败，端口：{Port}", request.Port);
                return Results.Problem(statusCode: 500, title: "端口设置失败", detail: "无法保存端口设置");
            }
        });

        endpoints.MapPatch("/api/settings/startup-mode", (StartupModeRequest request, ILoggerFactory loggerFactory) =>
        {
            if (!Enum.IsDefined(request.StartupMode)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["startupMode"] = ["开机启动模式无效"] });
            try
            {
                var settings = UpdateStartupSettings(configurationFile, current => current with { StartupMode = request.StartupMode },
                    startupTasks.Apply, loggerFactory.CreateLogger(typeof(SettingsEndpointRouteBuilderExtensions).FullName!));
                return Results.Ok(ToResponse(settings));
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger(typeof(SettingsEndpointRouteBuilderExtensions).FullName!).LogError(exception, "更新开机启动设置失败");
                return Results.Problem(statusCode: 500, title: "开机启动设置失败", detail: "无法保存配置或更新 Windows 计划任务，请检查目录权限和程序日志");
            }
        });

        endpoints.MapPut("/api/settings", (SettingsRequest request, SecurityEventNotifier securityEvents, ILoggerFactory loggerFactory) =>
        {
            if (request.Port is < 1 or > 65535)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["port"] = ["端口必须在 1 到 65535 之间"]
                });
            }
            if (!Enum.IsDefined(request.StartupMode)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["startupMode"] = ["开机启动模式无效"] });
            try
            {
                var changed = false;
                var settings = UpdateStartupSettings(configurationFile, current =>
                {
                    changed = current.LanAccessEnabled != request.LanAccessEnabled;
                    return current with { Port = request.Port, LanAccessEnabled = request.LanAccessEnabled, StartupMode = request.StartupMode };
                }, startupTasks.Apply, loggerFactory.CreateLogger(typeof(SettingsEndpointRouteBuilderExtensions).FullName!));
                if (changed) securityEvents.Record(SecurityEventType.LanAccessChanged, enabled: request.LanAccessEnabled);
                loggerFactory.CreateLogger(typeof(SettingsEndpointRouteBuilderExtensions).FullName!)
                    .LogInformation("设置已保存，重启 WinPilot 后生效");

                return Results.Ok(ToResponse(settings));
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger(typeof(SettingsEndpointRouteBuilderExtensions).FullName!)
                    .LogError(exception, "保存设置失败");
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "保存设置失败", detail: "无法保存配置或更新 Windows 计划任务，请检查目录权限和程序日志");
            }
        });

        return endpoints;
    }

    internal static WinPilotSettings UpdateStartupSettings(string configurationFile,
        Func<WinPilotSettings, WinPilotSettings> update, Action<StartupMode> apply, ILogger logger)
    {
        lock (startupSettingsGate)
        {
            var previousMode = StartupMode.Disabled;
            var attempted = false;
            try
            {
                return WinPilotSettings.Update(configurationFile, current =>
                {
                    var next = update(current);
                    if (!Enum.IsDefined(next.StartupMode) || next.Port is < 1 or > 65535)
                        throw new InvalidDataException("配置内容不符合当前 WinPilot 版本要求");
                    previousMode = current.StartupMode;
                    attempted = true;
                    apply(next.StartupMode);
                    return next;
                });
            }
            catch
            {
                if (attempted)
                {
                    try { apply(previousMode); }
                    catch (Exception exception) { logger.LogError(exception, "恢复原开机启动设置失败，请检查 Windows 计划任务"); }
                }
                throw;
            }
        }
    }

    private static object ToResponse(WinPilotSettings settings) => new
    {
        port = settings.Port,
        lanAccessEnabled = settings.LanAccessEnabled,
        startupMode = settings.StartupMode
    };

    private sealed record SettingsRequest(int Port, bool LanAccessEnabled, StartupMode StartupMode);
    private sealed record LanAccessRequest(bool Enabled);
    private sealed record PortRequest(int Port);
    private sealed record StartupModeRequest(StartupMode StartupMode);
    private sealed record UnauthenticatedWebPageRequest(bool Enabled);
}
