using System.Text.Json;
using System.Text.Json.Serialization;
using WinPilot.Core;
using WinPilot.Core.Tasks;
using WinPilot.Features.Plugins;
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
        endpoints.MapGet("/api/tasks/variables", (TaskVariableCatalog variables) => Results.Ok(variables.GetAll()));
        endpoints.MapGet("/api/tasks/extensions/triggers", ([Microsoft.AspNetCore.Mvc.FromServices] PluginTaskExtensionRegistry extensions) => Results.Ok(extensions.GetTriggers()));
        endpoints.MapGet("/api/tasks/extensions/actions", ([Microsoft.AspNetCore.Mvc.FromServices] PluginTaskExtensionRegistry extensions) => Results.Ok(extensions.GetActions().Select(action => new { action.PluginId, action.PluginDisplayName, action.ActionId, action.ActionName, action.Parameters, action.Available, DynamicOptions = action.OptionsResolver is not null })));
        endpoints.MapGet("/api/tasks/extensions/actions/{pluginId}/{actionId}/options/{parameterKey}", async (string pluginId, string actionId, string parameterKey, [Microsoft.AspNetCore.Mvc.FromServices] PluginTaskExtensionRegistry extensions, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await extensions.GetActionOptionsAsync(pluginId, actionId, parameterKey, cancellationToken)); }
            catch (FileNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "插件操作失败", detail: exception.Message); }
        });
        endpoints.MapPost("/api/tasks", (WinPilotTask task, TaskService tasks, HttpContext context) => Execute(() =>
        {
            if (ValidateTaskStructure(task) is { } error) return error;
            var saved = tasks.Create(task);
            context.Response.Headers.Location = "/api/tasks/" + Uri.EscapeDataString(saved.Name);
            return Results.Json(TaskPersistenceFile.From(saved), JsonOptions, statusCode: StatusCodes.Status201Created);
        }));
        endpoints.MapPut("/api/tasks/{name}", (string name, WinPilotTask task, TaskService tasks) => Execute(() =>
        {
            if (ValidateTaskStructure(task) is { } error) return error;
            var saved = tasks.Update(name, task);
            return Results.Json(TaskPersistenceFile.From(saved), JsonOptions);
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

    private static IResult? ValidateTaskStructure(WinPilotTask task)
    {
        var errors = new Dictionary<string, string[]>();
        if (task.Trigger is null) errors["trigger"] = ["触发条件不能为空"];
        if (task.Actions is null) errors["actions"] = ["任务动作不能为空"];
        else
            for (var index = 0; index < task.Actions.Count; index++)
                if (task.Actions[index] is null) errors[$"actions[{index}]"] = ["动作不能为空"];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static IResult Execute(Func<IResult> action)
    {
        try { return action(); }
        catch (ResourceNameConflictException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "操作失败", detail: exception.Message);
        }
        catch (FileNotFoundException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "操作失败", detail: exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(statusCode: 400, title: "操作失败", detail: exception.Message);
        }
    }

    private sealed record TaskEnabledRequest(bool Enabled);
}
