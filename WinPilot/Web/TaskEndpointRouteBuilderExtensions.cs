using System.Text.Json;
using System.Text.Json.Serialization;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;

namespace WinPilot.Web;

public static class TaskEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tasks", (TaskService tasks) => Results.Json(tasks.GetAll().Select(TaskPersistenceFile.From), JsonOptions));
        endpoints.MapGet("/api/task-variables", (TaskVariableCatalog variables) => Results.Ok(variables.GetAll()));
        endpoints.MapPost("/api/tasks", (WinPilotTask task, TaskService tasks, HttpContext context) => Execute(() =>
        {
            tasks.Create(task);
            context.Response.Headers.Location = "/api/tasks/" + Uri.EscapeDataString(task.Name);
            return Results.Json(TaskPersistenceFile.From(task), JsonOptions, statusCode: StatusCodes.Status201Created);
        }));
        endpoints.MapPut("/api/tasks/{name}", (string name, WinPilotTask task, TaskService tasks) => Execute(() =>
        {
            tasks.Update(name, task);
            return Results.Json(TaskPersistenceFile.From(task), JsonOptions);
        }));
        endpoints.MapPatch("/api/tasks/{name}/enabled", (string name, TaskEnabledRequest request, TaskService tasks) =>
            Execute(() => Results.Json(TaskPersistenceFile.From(tasks.SetEnabled(name, request.Enabled)), JsonOptions)));
        endpoints.MapDelete("/api/tasks/{name}", (string name, TaskService tasks) => Execute(() =>
        {
            tasks.Delete(name);
            return Results.NoContent();
        }));
        endpoints.MapPost("/api/tasks/{name}/run", (string name, TaskService tasks) => Execute(() =>
        {
            tasks.RunNow(name);
            return Results.NoContent();
        }));
        return endpoints;
    }

    private static IResult Execute(Func<IResult> action)
    {
        try { return action(); }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        {
            return Results.Problem(statusCode: 400, title: "操作失败", detail: exception.Message);
        }
    }

    private sealed record TaskEnabledRequest(bool Enabled);
}
