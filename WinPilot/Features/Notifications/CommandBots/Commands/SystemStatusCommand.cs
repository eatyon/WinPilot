using System.Globalization;
using WinPilot.Core.SystemStatus;

namespace WinPilot.Features.Notifications.CommandBots.Commands;

internal sealed class SystemStatusCommand(ISystemResourceMonitor? monitor)
{
    internal SystemResourceStatus Capture(CancellationToken token) =>
        (monitor ?? throw new InvalidOperationException("系统状态服务不可用")).Capture(token);

    internal static string FormatText(IReadOnlyList<CommandImageRenderer.Row> rows) =>
        "WinPilot 系统状态\n\n" + string.Join("\n", rows.Select(row => $"{row.Name}：{row.Description}"));

    internal static IReadOnlyList<CommandImageRenderer.Row> FormatRows(SystemResourceStatus status)
    {
        var rows = new List<CommandImageRenderer.Row>
        {
            new("CPU", Percent(status.CpuUsagePercent)),
            new("内存", Percent(status.MemoryUsagePercent))
        };
        if (status.GpuUsages.Count == 0) rows.Add(new("GPU", "不可用"));
        else rows.AddRange(status.GpuUsages.Select(item => new CommandImageRenderer.Row(item.Name, Percent(item.UsagePercent))));
        if (status.DiskUsages.Count == 0) rows.Add(new("磁盘", "不可用"));
        else rows.AddRange(status.DiskUsages.Select(item => new CommandImageRenderer.Row(item.Name, Percent(item.UsagePercent))));
        return rows;
    }

    private static string Percent(double? value) => value is { } number && double.IsFinite(number) && number is >= 0 and <= 100
        ? number.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "不可用";
}
