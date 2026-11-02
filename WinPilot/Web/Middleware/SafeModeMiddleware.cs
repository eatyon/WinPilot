using WinPilot.Features.Security;

namespace WinPilot.Web;

public sealed class SafeModeMiddleware(RequestDelegate next)
{
    private static readonly Dictionary<string, (string[] Blacklist, string[] Exceptions)> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["All"] = (
            Blacklist: ["/api/files/*", "/api/terminal/*", "/api/settings/notifications/*"],
            Exceptions: []),
        [HttpMethods.Get] = (
            Blacklist: ["/api/programs/*"],
            Exceptions: ["/api/programs/tags"]),
        [HttpMethods.Post] = (
            Blacklist: ["/api/settings/*", "/api/desktop/keyboard", "/api/programs/*", "/api/tasks"],
            Exceptions: ["/api/programs/tags/{name}/launch"]),
        [HttpMethods.Put] = (
            Blacklist: ["/api/settings/*", "/api/programs/*", "/api/tasks/*", "/api/plugins/*"],
            Exceptions: []),
        [HttpMethods.Patch] = (
            Blacklist: ["/api/settings/*"],
            Exceptions: []),
        [HttpMethods.Delete] = (
            Blacklist: ["/api/settings/*", "/api/programs/*", "/api/tasks/*", "/api/plugins/*"],
            Exceptions: []),
        ["Other"] = (
            Blacklist: ["/api/settings/*"],
            Exceptions: [])
    };

    public async Task InvokeAsync(HttpContext context, SafeModeService safeMode)
    {
        if (!safeMode.Enabled || !IsBlocked(context.Request))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "safe_mode_restricted",
            message = "安全模式下不可执行此操作"
        });
    }

    private static bool IsBlocked(HttpRequest request)
    {
        var method = HttpMethods.IsHead(request.Method) ? HttpMethods.Get : request.Method;
        var route = (request.HttpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var common = Rules["All"];
        var rules = method.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? Rules["Other"]
            : Rules.GetValueOrDefault(method, Rules["Other"]);
        var blocked = MatchesAny(common.Blacklist, request.Path, route) || MatchesAny(rules.Blacklist, request.Path, route);
        return blocked && !MatchesAny(common.Exceptions, request.Path, route) && !MatchesAny(rules.Exceptions, request.Path, route);
    }

    internal static bool MatchesAny(string[] patterns, PathString path, string? route)
    {
        var value = (path.Value ?? "").TrimEnd('/');
        foreach (var pattern in patterns)
        {
            var candidate = pattern.Contains('{') ? route?.TrimEnd('/') : value;
            if (candidate is null) continue;
            if (pattern.EndsWith("/*", StringComparison.Ordinal))
            {
                var prefix = pattern[..^2];
                if (candidate.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || (candidate.Length > prefix.Length
                        && candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && candidate[prefix.Length] == '/')) return true;
            }
            else if (string.Equals(pattern.TrimEnd('/'), candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
