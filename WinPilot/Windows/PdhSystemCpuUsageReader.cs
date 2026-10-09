using System.Runtime.InteropServices;

namespace WinPilot.Windows;

internal static class PdhSystemCpuUsageReader
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhCStatusValidData = 0;
    private const uint PdhCStatusNewData = 1;
    private const int SampleIntervalMilliseconds = 750;

    public static double ReadUsage(CancellationToken cancellationToken)
    {
        IntPtr query = IntPtr.Zero;
        try
        {
            ThrowIfFailed(PdhOpenQueryW(null, IntPtr.Zero, out query), "无法打开 CPU 性能计数器查询");
            ThrowIfFailed(PdhAddEnglishCounterW(query, "\\Processor Information(_Total)\\% Processor Utility", IntPtr.Zero, out var counter), "无法添加 CPU 使用率计数器");
            ThrowIfFailed(PdhCollectQueryData(query), "无法采集 CPU 使用率的首次样本");
            if (cancellationToken.WaitHandle.WaitOne(SampleIntervalMilliseconds)) cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFailed(PdhCollectQueryData(query), "无法采集 CPU 使用率的第二次样本");

            ThrowIfFailed(PdhGetFormattedCounterValue(counter, PdhFmtDouble, out var valueType, out var value), "无法读取 CPU 使用率计数器");
            if (value.Status is not (PdhCStatusValidData or PdhCStatusNewData) || !double.IsFinite(value.DoubleValue) || value.DoubleValue < 0)
            {
                throw new PdhCpuUsageUnavailableException("CPU 使用率计数器未返回有效样本");
            }

            return Math.Clamp(value.DoubleValue, 0, 100);
        }
        finally
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
        }
    }

    private static void ThrowIfFailed(uint status, string operation)
    {
        if (status != 0) throw new PdhCpuUsageUnavailableException($"{operation}，PDH 状态码：0x{status:X8}");
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
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint valueType, out PdhFmtCounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

internal sealed class PdhCpuUsageUnavailableException(string message) : Exception(message);
