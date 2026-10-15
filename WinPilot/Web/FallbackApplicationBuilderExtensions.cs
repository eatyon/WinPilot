using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;

namespace WinPilot.Web;

public static class FallbackApplicationBuilderExtensions
{
    private static readonly NonFileNameRouteConstraint NonFileName = new();

    public static IApplicationBuilder UseWebFallback(this IApplicationBuilder app, StaticFileOptions? staticFileOptions = null)
    {
        var serveSpa = staticFileOptions is null ? null : app.New().UseStaticFiles(staticFileOptions).Build();
        return app.Use(async (context, next) =>
        {
            await next(context);
            if (context.Response.HasStarted) return;
            var endpoint = context.GetEndpoint();
            var unmatched = endpoint is null && context.Response.StatusCode == StatusCodes.Status404NotFound;
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                if (unmatched)
                    await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "API 不存在").ExecuteAsync(context);
                return;
            }

            var routingMethodRejected = endpoint is not null and not RouteEndpoint
                && context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed;
            if (serveSpa is null || (!unmatched && !routingMethodRejected)
                || (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                || !NonFileName.Match(context, null, "path", new RouteValueDictionary { ["path"] = context.Request.Path.Value }, RouteDirection.IncomingRequest))
                return;

            var originalPath = context.Request.Path;
            context.Request.Path = "/index.html";
            context.SetEndpoint(null);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers.Remove("Allow");
            try { await serveSpa(context); }
            finally
            {
                context.Request.Path = originalPath;
                context.SetEndpoint(endpoint);
            }
        });
    }
}
