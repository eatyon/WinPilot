using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WinPilot.Windows;

internal static partial class PdhGpuUsageReader
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCStatusValidData = 0;
    private const uint PdhCStatusNewData = 1;
    private const int SampleIntervalMilliseconds = 750;

    public static PdhGpuUsageSample ReadUsage(IReadOnlyCollection<int> processIds, CancellationToken cancellationToken)
    {
        var samples = ReadSamples(cancellationToken);
        var usagesByAdapter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var matchingItemCount = 0;
        var validValueCount = 0;
        foreach (var sample in samples)
        {
            if (!TryGetProcessAndAdapter(sample.InstanceName, out var processId, out var adapter) || !processIds.Contains(processId)) continue;
            matchingItemCount++;
            if (!sample.IsValid) continue;
            validValueCount++;
            usagesByAdapter[adapter] = Math.Max(usagesByAdapter.GetValueOrDefault(adapter), sample.UsagePercent);
        }

        if (matchingItemCount > 0 && validValueCount == 0) throw new PdhGpuUsageUnavailableException("GPU Engine 计数器未返回目标进程的有效样本");
        if (usagesByAdapter.Count == 0) return new PdhGpuUsageSample(0, matchingItemCount, validValueCount, 0);

        return new PdhGpuUsageSample(usagesByAdapter.Values.Max(), matchingItemCount, validValueCount, usagesByAdapter.Count);
    }

    public static IReadOnlyList<GpuAdapterUsage> ReadSystemUsages(CancellationToken cancellationToken)
    {
        var usagesByAdapter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in ReadSamples(cancellationToken))
        {
            if (!TryGetProcessAndAdapter(sample.InstanceName, out _, out var adapter) || !sample.IsValid) continue;
            usagesByAdapter[adapter] = Math.Max(usagesByAdapter.GetValueOrDefault(adapter), sample.UsagePercent);
        }

        if (usagesByAdapter.Count == 0) throw new PdhGpuUsageUnavailableException("GPU Engine 计数器未返回有效样本");
        return usagesByAdapter.Select(pair => new GpuAdapterUsage(pair.Key, Math.Clamp(pair.Value, 0, 100))).ToArray();
    }

    private static IReadOnlyList<GpuEngineSample> ReadSamples(CancellationToken cancellationToken)
    {
        IntPtr query = IntPtr.Zero;
        try
        {
            ThrowIfFailed(PdhOpenQueryW(null, IntPtr.Zero, out query), "无法打开 GPU 性能计数器查询");
            ThrowIfFailed(PdhAddEnglishCounterW(query, "\\GPU Engine(*)\\Utilization Percentage", IntPtr.Zero, out var counter), "无法添加 GPU Engine 利用率计数器");
            ThrowIfFailed(PdhCollectQueryData(query), "无法采集 GPU 使用率的首次样本");
            if (cancellationToken.WaitHandle.WaitOne(SampleIntervalMilliseconds)) cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFailed(PdhCollectQueryData(query), "无法采集 GPU 使用率的第二次样本");

            return ReadCounterValues(counter);
        }
        finally
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
        }
    }

    private static IReadOnlyList<GpuEngineSample> ReadCounterValues(IntPtr counter)
    {
        uint bufferSize = 0;
        uint itemCount = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != PdhMoreData) ThrowIfFailed(status, "无法读取 GPU Engine 利用率计数器");

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            ThrowIfFailed(PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bufferSize, ref itemCount, buffer), "无法读取 GPU Engine 利用率计数器");
            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItemDouble>();
            var samples = new List<GpuEngineSample>(checked((int)itemCount));
            for (var index = 0; index < itemCount; index++)
            {
                var itemPointer = IntPtr.Add(buffer, checked(index * itemSize));
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItemDouble>(itemPointer);
                var instanceName = Marshal.PtrToStringUni(item.Name);
                if (instanceName is null) continue;
                var isValid = item.Value.Status is PdhCStatusValidData or PdhCStatusNewData && double.IsFinite(item.Value.DoubleValue) && item.Value.DoubleValue >= 0;
                samples.Add(new GpuEngineSample(instanceName, isValid ? item.Value.DoubleValue : 0, isValid));
            }
            return samples;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetProcessAndAdapter(string instanceName, out int processId, out string adapter)
    {
        processId = 0;
        var match = GpuEngineInstancePattern().Match(instanceName);
        if (!match.Success || !int.TryParse(match.Groups["processId"].Value, out processId))
        {
            adapter = string.Empty;
            return false;
        }

        adapter = match.Groups["adapter"].Value;
        return true;
    }

    private static void ThrowIfFailed(uint status, string operation)
    {
        if (status != 0) throw new PdhGpuUsageUnavailableException($"{operation}，PDH 状态码：0x{status:X8}");
    }

    [GeneratedRegex("pid_(?<processId>\\d+)_luid_(?<adapter>0x[0-9a-f]+_0x[0-9a-f]+)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GpuEngineInstancePattern();

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

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

internal sealed record PdhGpuUsageSample(double UsagePercent, int MatchingItemCount, int ValidValueCount, int AdapterCount);
internal sealed record GpuEngineSample(string InstanceName, double UsagePercent, bool IsValid);
internal sealed record GpuAdapterUsage(string AdapterLuid, double UsagePercent);
internal sealed class PdhGpuUsageUnavailableException(string message) : Exception(message);
