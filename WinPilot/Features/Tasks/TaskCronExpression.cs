using Cronos;

namespace WinPilot.Features.Tasks;

internal static class TaskCronExpression
{
    internal static CronExpression Parse(string expression) => CronExpression.Parse(expression, CronFormat.IncludeSeconds);
}
