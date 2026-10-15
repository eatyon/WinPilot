using WinPilot.Core;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;

namespace WinPilot.Web;

public static class ProgramEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapProgramEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/programs/users", (IProgramManager manager) => Results.Ok(manager.GetInteractiveUsers().Select(user => new { userName = user.UserName, isCurrentUser = user.IsCurrentUser })));
        endpoints.MapGet("/api/programs", (string userName, IProgramManager manager) => Execute(() => Results.Ok(manager.GetRunningPrograms(userName).Select(program => new { processId = program.ProcessId, name = program.Name, userName = program.UserName, sessionId = program.SessionId }))));
        endpoints.MapPost("/api/programs/usage", (ProcessSelectionRequest request, HttpContext context, IProgramManager manager) => Execute(() => Results.Ok(manager.GetUsage(request.ProcessIds, context.RequestAborted))));
        endpoints.MapPost("/api/programs/close", (ProcessCloseRequest request, IProgramManager manager, ILoggerFactory loggerFactory) => Execute(() =>
        {
            manager.Close(request.ProcessIds, request.Force);
            loggerFactory.CreateLogger(typeof(ProgramEndpointRouteBuilderExtensions).FullName!).LogInformation("用户程序关闭请求已完成，共 {ProcessCount} 个进程，强制关闭：{Force}", request.ProcessIds.Count, request.Force);
            return Results.NoContent();
        }));
        endpoints.MapPost("/api/programs/launch", (ProgramLaunchRequest request, IProgramManager manager, ILoggerFactory loggerFactory) => Execute(() =>
        {
            manager.Launch(request);
            loggerFactory.CreateLogger(typeof(ProgramEndpointRouteBuilderExtensions).FullName!).LogInformation("已请求启动程序：{ProgramPath}", request.ProgramPath);
            return Results.NoContent();
        }));

        endpoints.MapGet("/api/programs/tags", (ProgramTagService service) => Results.Ok(service.GetAll()));
        endpoints.MapPost("/api/programs/tags", (ProgramTag tag, ProgramTagService service) => Execute(() => { service.Create(tag); return Results.Created($"/api/programs/tags/{Uri.EscapeDataString(tag.Name)}", tag); }, tagResource: true));
        endpoints.MapPut("/api/programs/tags/{name}", (string name, ProgramTag tag, ProgramTagService service) => Execute(() => { service.Update(name, tag); return Results.Ok(tag); }, tagResource: true));
        endpoints.MapDelete("/api/programs/tags/{name}", (string name, ProgramTagService service) => Execute(() => { service.Delete(name); return Results.NoContent(); }, tagResource: true));
        endpoints.MapPost("/api/programs/tags/{name}/launch", (string name, ProgramTagService service, IProgramManager manager) => Execute(() =>
        {
            var tag = service.Get(name);
            return Execute(() =>
            {
                manager.Launch(new ProgramLaunchRequest(tag.ProgramPath, tag.Arguments, tag.WorkingDirectory, tag.RunElevated, tag.Silent));
                return Results.NoContent();
            });
        }, tagResource: true));
        return endpoints;
    }

    private static IResult Execute(Func<IResult> action, bool tagResource = false)
    {
        try { return action(); }
        catch (ResourceNameConflictException exception) when (tagResource) { return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "操作失败", detail: exception.Message); }
        catch (FileNotFoundException exception) when (tagResource) { return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "操作失败", detail: exception.Message); }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException or DirectoryNotFoundException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "操作失败", detail: exception.Message); }
    }

    private sealed record ProcessSelectionRequest(IReadOnlyList<int> ProcessIds);
    private sealed record ProcessCloseRequest(IReadOnlyList<int> ProcessIds, bool Force);
}
