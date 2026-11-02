namespace WinPilot.Core.SystemStatus;

public sealed record SystemResourceStatus(
    double CpuUsagePercent,
    double MemoryUsagePercent,
    IReadOnlyList<SystemResourceUsage> GpuUsages,
    IReadOnlyList<SystemResourceUsage> DiskUsages);

public sealed record SystemResourceUsage(string Name, double? UsagePercent);
