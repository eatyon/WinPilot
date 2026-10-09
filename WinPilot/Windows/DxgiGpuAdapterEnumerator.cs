using System.Runtime.InteropServices;

namespace WinPilot.Windows;

internal static class DxgiGpuAdapterEnumerator
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private static readonly Guid Factory1InterfaceId = new("770AAE78-F26F-4DBA-A829-253C83D1B387");

    public static IReadOnlyDictionary<string, int> GetAdapterIndexes()
    {
        object? factoryObject = null;
        try
        {
            var factoryInterfaceId = Factory1InterfaceId;
            var result = CreateDXGIFactory1(ref factoryInterfaceId, out factoryObject);
            if (result < 0 || factoryObject is not IDXGIFactory1 factory) return new Dictionary<string, int>();

            var virtualAdapterNames = GetVirtualDisplayAdapterNames();
            var adapters = new List<DxgiGpuAdapter>();
            for (uint index = 0; ; index++)
            {
                var enumResult = factory.EnumAdapters1(index, out var adapter);
                if (enumResult == DxgiErrorNotFound) break;
                if (enumResult < 0 || adapter is null) continue;

                try
                {
                    if (adapter.GetDesc1(out var description) >= 0 && (description.Flags & 2) == 0)
                    {
                        var name = (description.Description ?? string.Empty).TrimEnd('\0');
                        if (!virtualAdapterNames.Contains(name)) adapters.Add(new DxgiGpuAdapter(FormatLuid(description.AdapterLuid), checked((int)index)));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            return adapters
                .OrderBy(adapter => adapter.DxgiIndex)
                .GroupBy(adapter => adapter.Luid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select((adapter, displayIndex) => new KeyValuePair<string, int>(adapter.Luid, displayIndex))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (COMException)
        {
            return new Dictionary<string, int>();
        }
        finally
        {
            if (factoryObject is not null) Marshal.ReleaseComObject(factoryObject);
        }
    }

    private static string FormatLuid(Luid luid) => $"0x{unchecked((uint)luid.HighPart):x8}_0x{luid.LowPart:x8}";

    private static HashSet<string> GetVirtualDisplayAdapterNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevicesW(null, index, ref device, 0)) break;
            if ((device.DeviceId ?? string.Empty).StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) names.Add(device.DeviceString ?? string.Empty);
        }
        return names;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object factory);

    [ComImport, Guid("770AAE78-F26F-4DBA-A829-253C83D1B387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr adapterObject);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr description, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 adapterObject);
        [return: MarshalAs(UnmanagedType.Bool)] bool IsCurrent();
    }

    [ComImport, Guid("29038F61-3839-4626-91FD-086879011A05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint output, out IntPtr outputObject);
        [PreserveSig] int GetDesc(out IntPtr description);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        [PreserveSig] int GetDesc1(out DxgiAdapterDescription description);
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 320)]
    private struct DxgiAdapterDescription
    {
        [FieldOffset(0), MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Description;
        [FieldOffset(296)] public Luid AdapterLuid;
        [FieldOffset(304)] public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? deviceName, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    private sealed record DxgiGpuAdapter(string Luid, int DxgiIndex);
}
