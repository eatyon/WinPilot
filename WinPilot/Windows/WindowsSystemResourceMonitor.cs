using System.Runtime.InteropServices;
using WinPilot.Core.SystemStatus;

namespace WinPilot.Windows;

public sealed class WindowsSystemResourceMonitor(ILogger<WindowsSystemResourceMonitor> logger) : ISystemResourceMonitor
{
    private const int SampleIntervalMilliseconds = 750;
    private int cpuFallbackState;
    private int cpuCaptureFailed;
    private int gpuFailureState;
    private int diskFailureState;

    public SystemResourceStatus Capture(CancellationToken cancellationToken)
    {
        var memory = ReadMemoryStatus();
        var cpuTask = Task.Run(() => ReadCpuUsage(cancellationToken), cancellationToken);
        var gpuUsages = ReadGpuUsages(cancellationToken);
        var cpuUsage = GetTaskResult(cpuTask, "CPU");
        var diskUsages = ReadDiskUsages(cancellationToken);
        return new SystemResourceStatus(cpuUsage, CalculateMemoryUsage(memory), gpuUsages, diskUsages);
    }

    private T GetTaskResult<T>(Task<T> task, string resourceName)
    {
        try
        {
            var result = task.GetAwaiter().GetResult();
            if (Interlocked.Exchange(ref cpuCaptureFailed, 0) != 0) logger.LogInformation("系统 {ResourceName} 采集已恢复", resourceName);
            return result;
        }
        catch (OperationCanceledException) when (task.IsCanceled) { throw; }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref cpuCaptureFailed, 1) == 0) logger.LogError(exception, "系统 {ResourceName} 采集任务失败", resourceName);
            throw;
        }
    }

    private static CpuTimes ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new InvalidOperationException($"无法读取系统 CPU 使用率，Windows 错误代码：{Marshal.GetLastWin32Error()}");
        return new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private IReadOnlyList<SystemResourceUsage> ReadGpuUsages(CancellationToken cancellationToken)
    {
        try
        {
            var adapterIndexes = DxgiGpuAdapterEnumerator.GetAdapterIndexes();
            var rawUsages = PdhGpuUsageReader.ReadSystemUsages(cancellationToken);
            var usages = (adapterIndexes.Count == 0 ? rawUsages : rawUsages.Where(usage => adapterIndexes.ContainsKey(usage.AdapterLuid)))
                .OrderBy(usage => adapterIndexes.TryGetValue(usage.AdapterLuid, out var index) ? index : int.MaxValue)
                .ThenBy(usage => usage.AdapterLuid, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var result = usages.Select((usage, fallbackIndex) =>
            {
                var index = adapterIndexes.TryGetValue(usage.AdapterLuid, out var mappedIndex) ? mappedIndex : fallbackIndex;
                return new SystemResourceUsage($"GPU {index}", usage.UsagePercent);
            }).ToArray();
            if (Interlocked.Exchange(ref gpuFailureState, 0) != 0) logger.LogInformation("系统 GPU 使用率采集已恢复");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PdhGpuUsageUnavailableException exception)
        {
            if (Interlocked.Exchange(ref gpuFailureState, 1) != 1) logger.LogDebug(exception, "系统 GPU 使用率不可用");
            return [new SystemResourceUsage("GPU 0", null)];
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref gpuFailureState, 2) != 2) logger.LogWarning(exception, "无法枚举系统 GPU 适配器");
            return [new SystemResourceUsage("GPU 0", null)];
        }
    }

    private IReadOnlyList<SystemResourceUsage> ReadDiskUsages(CancellationToken cancellationToken)
    {
        try
        {
            var usages = PdhSystemDiskUsageReader.ReadUsages(cancellationToken)
                .Select(usage => new { Usage = usage, DiskNumber = TryGetDiskNumber(usage.InstanceName) })
                .OrderBy(usage => usage.DiskNumber ?? int.MaxValue)
                .ThenBy(usage => usage.Usage.InstanceName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var result = usages.Select((usage, fallbackIndex) =>
            {
                var index = usage.DiskNumber ?? fallbackIndex;
                return new SystemResourceUsage($"磁盘 {index}", usage.Usage.UsagePercent);
            }).ToArray();
            if (Interlocked.Exchange(ref diskFailureState, 0) != 0) logger.LogInformation("系统磁盘活动时间采集已恢复");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PdhDiskUsageUnavailableException exception)
        {
            if (Interlocked.Exchange(ref diskFailureState, 1) != 1) logger.LogDebug(exception, "系统磁盘活动时间不可用");
            return [new SystemResourceUsage("磁盘 0", null)];
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref diskFailureState, 2) != 2) logger.LogWarning(exception, "无法枚举系统物理磁盘");
            return [new SystemResourceUsage("磁盘 0", null)];
        }
    }

    private static int? TryGetDiskNumber(string instanceName)
    {
        var separatorIndex = instanceName.IndexOf(' ');
        var number = separatorIndex < 0 ? instanceName : instanceName[..separatorIndex];
        return int.TryParse(number, out var value) ? value : null;
    }

    private double ReadCpuUsage(CancellationToken cancellationToken)
    {
        try
        {
            var usage = PdhSystemCpuUsageReader.ReadUsage(cancellationToken);
            if (Interlocked.Exchange(ref cpuFallbackState, 0) != 0) logger.LogInformation("任务管理器 CPU 使用率计数器已恢复");
            return usage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PdhCpuUsageUnavailableException exception)
        {
            if (Interlocked.Exchange(ref cpuFallbackState, 1) != 1) logger.LogWarning(exception, "CPU 使用率计数器不可用，已改用处理器运行时间计算，数值可能与任务管理器不同");
            var first = ReadCpuTimes();
            if (cancellationToken.WaitHandle.WaitOne(SampleIntervalMilliseconds)) cancellationToken.ThrowIfCancellationRequested();
            return CalculateCpuUsage(first, ReadCpuTimes());
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref cpuFallbackState, 2) != 2) logger.LogWarning(exception, "无法读取 CPU 使用率计数器，已改用处理器运行时间计算，数值可能与任务管理器不同");
            var first = ReadCpuTimes();
            if (cancellationToken.WaitHandle.WaitOne(SampleIntervalMilliseconds)) cancellationToken.ThrowIfCancellationRequested();
            return CalculateCpuUsage(first, ReadCpuTimes());
        }
    }

    private static MemoryStatus ReadMemoryStatus()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException($"无法读取系统内存使用率，Windows 错误代码：{Marshal.GetLastWin32Error()}");
        return status;
    }

    private static double CalculateCpuUsage(CpuTimes first, CpuTimes second)
    {
        var totalDelta = (second.Kernel + second.User) - (first.Kernel + first.User);
        var idleDelta = second.Idle - first.Idle;
        return totalDelta <= 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static double CalculateMemoryUsage(MemoryStatus status) => status.TotalPhysical == 0 ? 0 : Math.Clamp((status.TotalPhysical - status.AvailablePhysical) * 100d / status.TotalPhysical, 0, 100);
    private static ulong ToUInt64(FileTime value) => ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint LowDateTime; public uint HighDateTime; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length; public uint MemoryLoad; public ulong TotalPhysical; public ulong AvailablePhysical; public ulong TotalPageFile; public ulong AvailablePageFile; public ulong TotalVirtual; public ulong AvailableVirtual; public ulong AvailableExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
