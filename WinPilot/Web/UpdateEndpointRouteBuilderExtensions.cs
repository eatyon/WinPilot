using WinPilot.Core;
using WinPilot.Features.Updates;

namespace WinPilot.Web;

public static class UpdateEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/update/settings", (UpdateCheckService updates) => Results.Ok(updates.GetSettings()));
        endpoints.MapPut("/api/update/settings", async (UpdateSettings settings, UpdateCheckService updates, HttpContext context) =>
        {
            try { return Results.Ok(await updates.SaveSettingsAsync(settings, context.RequestAborted)); }
            catch (ArgumentException) { return Results.BadRequest(new { message = "更新设置无效" }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { message = exception.Message }); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Problem(statusCode: 500, title: "保存更新设置失败", detail: "无法写入配置文件，请检查文件权限");
            }
        });
        endpoints.MapPost("/api/update/check", async (UpdateCheckService updates, HttpContext context) => Results.Ok(await updates.CheckAsync(context.RequestAborted)));
        endpoints.MapGet("/api/update/status", (UpdateCheckService updates) => Results.Ok(updates.GetInstallStatus()));
        endpoints.MapPost("/api/update/install", async (UpdateCheckService updates, HttpContext context) =>
        {
            if (!await updates.TryStartInstallAsync(Environment.ProcessId, context.RequestAborted)) return Results.Conflict(new { message = "更新任务正在进行" });
            return Results.Accepted();
        });

        return endpoints;
    }
}
