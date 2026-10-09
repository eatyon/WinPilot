using WinPilot.Core.SystemStatus;

namespace WinPilot.Web;

public static class SystemStatusEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapSystemStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/system/status", (HttpContext context, ISystemResourceMonitor monitor) =>
        {
            var status = monitor.Capture(context.RequestAborted);
            return new { cpuUsagePercent = status.CpuUsagePercent, memoryUsagePercent = status.MemoryUsagePercent, gpuUsages = status.GpuUsages, diskUsages = status.DiskUsages };
        });

        return endpoints;
    }
}
