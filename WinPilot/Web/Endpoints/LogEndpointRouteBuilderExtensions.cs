using System.Globalization;
using WinPilot.Features.Logs;

namespace WinPilot.Web;

public static class LogEndpointRouteBuilderExtensions
{
    private static readonly HashSet<string> ValidLevels = ["TRC", "DBG", "INF", "WRN", "ERR", "FTL"];

    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/logs/dates", (LogQueryService service, ILoggerFactory loggerFactory) => Execute(() => Results.Ok(service.GetAvailableDates().Select(date => new
        {
            date = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        })), loggerFactory.CreateLogger("LogEndpoints")));

        endpoints.MapGet("/api/logs", (string? date, string? level, string? keyword, int? offset, int? limit, int? snapshot, HttpContext context, LogQueryService service, ILoggerFactory loggerFactory) =>
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var selectedDate))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["date"] = ["请选择有效的日志日期"] });
            }

            var selectedLevels = string.IsNullOrWhiteSpace(level)
                ? null
                : level.Split(',', StringSplitOptions.TrimEntries)
                    .Select(value => value.ToUpperInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedLevels is not null && selectedLevels.Any(value => !ValidLevels.Contains(value)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["level"] = ["日志级别无效"] });
            }

            try
            {
                return Execute(() =>
                {
                    var page = service.Query(new LogQuery(selectedDate, selectedLevels, keyword?.Trim(), offset ?? 0, limit ?? 500, snapshot), context.RequestAborted);
                    return Results.Ok(new
                    {
                        entries = page.Entries.Select(entry => new
                        {
                            id = entry.Id,
                            time = entry.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                            level = entry.Level,
                            source = entry.Source,
                            message = entry.Message,
                            details = entry.Details
                        }),
                        hasMore = page.HasMore,
                        snapshot = page.Snapshot
                    });
                }, loggerFactory.CreateLogger("LogEndpoints"));
            }
            catch (ArgumentOutOfRangeException exception)
            {
                var field = exception.ParamName?.ToLowerInvariant() ?? "offset";
                var message = field == "snapshot" ? "日志快照值不能小于 0"
                    : offset is < 0 ? "日志偏移量不能小于 0" : "最多可加载最近 10000 条历史日志";
                return Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
            }
        });

        return endpoints;
    }

    private static IResult Execute(Func<IResult> action, ILogger logger)
    {
        try { return action(); }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning("日志读取权限不足：{Reason}", exception.Message);
            return Results.Problem(statusCode: 403, title: "无法读取日志", detail: "没有访问日志文件的权限");
        }
        catch (IOException exception)
        {
            if ((exception.HResult & 0xFFFF) is 32 or 33)
                logger.LogDebug("日志文件正在被占用：{Reason}", exception.Message);
            else
                logger.LogError(exception, "日志读取失败");
            return Results.Problem(statusCode: 503, title: "无法读取日志", detail: "日志文件暂时无法读取，请稍后重试");
        }
    }
}
