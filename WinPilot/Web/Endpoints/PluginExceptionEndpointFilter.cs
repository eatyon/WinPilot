using WinPilot.Features.Plugins;

namespace WinPilot.Web;

internal sealed class PluginExceptionEndpointFilter(ILogger<PluginExceptionEndpointFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            if (exception is not PluginOperationException { Logged: true })
                logger.LogError(exception, "插件请求处理失败，请求路径：{Path}", context.HttpContext.Request.Path);
            if (context.HttpContext.Response.HasStarted) throw;
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "插件操作失败",
                detail: exception is PluginOperationException operation ? operation.Message : "插件操作失败，请检查程序日志");
        }
    }
}
