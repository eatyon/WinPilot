using Cronos;

namespace WinPilot.Features.Tasks;

/// <summary>
/// 统一解析包含秒字段的六段 Cron 表达式，供任务校验与调度使用。
/// </summary>
internal static class TaskCronExpression
{
    internal static CronExpression Parse(string expression) => CronExpression.Parse(expression, CronFormat.IncludeSeconds);
}
