using System.Runtime.InteropServices;

namespace WinPilot.Windows;

internal static class PdhSystemDiskUsageReader
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCStatusValidData = 0;
    private const uint PdhCStatusNewData = 1;
    private const int SampleIntervalMilliseconds = 750;

    public static IReadOnlyList<PhysicalDiskUsage> ReadUsages(CancellationToken cancellationToken)
    {
        IntPtr query = IntPtr.Zero;
        try
        {
            ThrowIfFailed(PdhOpenQueryW(null, IntPtr.Zero, out query), "无法打开磁盘性能计数器查询");
            ThrowIfFailed(PdhAddEnglishCounterW(query, "\\PhysicalDisk(*)\\% Disk Time", IntPtr.Zero, out var counter), "无法添加磁盘活动时间计数器");
            ThrowIfFailed(PdhCollectQueryData(query), "无法采集磁盘活动时间的首次样本");
            if (cancellationToken.WaitHandle.WaitOne(SampleIntervalMilliseconds)) cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFailed(PdhCollectQueryData(query), "无法采集磁盘活动时间的第二次样本");

            var values = ReadCounterValues(counter);
            if (values.Count == 0) throw new PdhDiskUsageUnavailableException("磁盘活动时间计数器未返回有效样本");

            return values;
        }
        finally
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
        }
    }

    private static List<PhysicalDiskUsage> ReadCounterValues(IntPtr counter)
    {
        uint bufferSize = 0;
        uint itemCount = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != PdhMoreData) ThrowIfFailed(status, "无法读取磁盘活动时间计数器");

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            ThrowIfFailed(PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bufferSize, ref itemCount, buffer), "无法读取磁盘活动时间计数器");
            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItemDouble>();
            var values = new List<PhysicalDiskUsage>(checked((int)itemCount));
            for (var index = 0; index < itemCount; index++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItemDouble>(IntPtr.Add(buffer, checked(index * itemSize)));
                var instanceName = Marshal.PtrToStringUni(item.Name);
                if (string.IsNullOrWhiteSpace(instanceName) || string.Equals(instanceName, "_Total", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.Value.Status is not (PdhCStatusValidData or PdhCStatusNewData) || !double.IsFinite(item.Value.DoubleValue) || item.Value.DoubleValue < 0) continue;
                values.Add(new PhysicalDiskUsage(instanceName, Math.Clamp(item.Value.DoubleValue, 0, 100)));
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ThrowIfFailed(uint status, string operation)
    {
        if (status != 0) throw new PdhDiskUsageUnavailableException($"{operation}，PDH 状态码：0x{status:X8}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItemDouble
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

internal sealed class PdhDiskUsageUnavailableException(string message) : Exception(message);
internal sealed record PhysicalDiskUsage(string InstanceName, double UsagePercent);
