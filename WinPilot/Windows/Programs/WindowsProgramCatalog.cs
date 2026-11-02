using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using WinPilot.Core.Programs;

namespace WinPilot.Windows;

/// <summary>
/// 为用户进程补充文件描述、包名称和窗口标题，再按窗口、包与父子关系生成展示分组。
/// </summary>
internal static class WindowsProgramCatalog
{
    private const int ExtendedWindowStyle = -20;
    private const int WindowStyle = -16;
    private const int ToolWindow = 0x80;
    private const int AppWindow = 0x40000;
    private const int OverlappedWindow = 0x300;
    private const int NoActivate = 0x08000000;
    private const int Caption = 0xC00000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint SnapshotProcesses = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;

    internal sealed record Entry(
        RunningProgram Program,
        int ParentId,
        long StartedAt,
        string DisplayName,
        string? Package,
        string? PackageName,
        string ExecutablePath)
    {
        public bool HasWindow => Program.WindowTitles.Count > 0;
    }

    public static IReadOnlyList<RunningProgram> Describe(IReadOnlyList<RunningProgram> programs)
    {
        var parents = ReadParents();
        var windows = ReadWindows();
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<Entry>(programs.Count);
        foreach (var program in programs)
        {
            var path = "";
            var startedAt = 0L;
            string? package = null;
            try
            {
                using var process = Process.GetProcessById(program.ProcessId);
                try
                {
                    startedAt = process.StartTime.ToUniversalTime().Ticks;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, program.ProcessId);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        var buffer = new StringBuilder(32768);
                        var length = buffer.Capacity;
                        if (QueryFullProcessImageNameW(handle, 0, buffer, ref length))
                        {
                            path = buffer.ToString();
                        }
                        uint size = 0;
                        if (GetPackageFullName(handle, ref size, null) == ErrorInsufficientBuffer)
                        {
                            var name = new StringBuilder(checked((int)size));
                            if (GetPackageFullName(handle, ref size, name) == 0)
                            {
                                package = name.ToString();
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            var displayName = program.Name;
            if (path.Length > 0)
            {
                if (!descriptions.TryGetValue(path, out displayName))
                {
                    try
                    {
                        displayName = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                    }
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = program.Name;
                    }
                    descriptions[path] = displayName;
                }
            }
            string? packageName = null;
            if (package is not null && !packages.TryGetValue(package, out packageName))
            {
                packageName = ReadPackageName(package);
                packages[package] = packageName;
            }
            entries.Add(new(program with { WindowTitles = windows.GetValueOrDefault(program.ProcessId) ?? [] },
                parents.GetValueOrDefault(program.ProcessId), startedAt, displayName, package, packageName, path));
        }
        return Group(entries);
    }

    internal static IReadOnlyList<RunningProgram> Group(IReadOnlyList<Entry> entries)
    {
        // 父进程必须属于同一用户和会话；启动时间用于排除父 PID 已被其他进程复用的情况。
        var byId = entries.ToDictionary(entry => entry.Program.ProcessId);
        Entry? Parent(Entry entry)
        {
            if (!byId.TryGetValue(entry.ParentId, out var parent)
                || parent.Program.SessionId != entry.Program.SessionId
                || !string.Equals(parent.Program.UserName, entry.Program.UserName, StringComparison.OrdinalIgnoreCase)
                || (entry.StartedAt != 0 && parent.StartedAt > entry.StartedAt))
            {
                return null;
            }
            return parent;
        }
        static bool IsExplorer(Entry entry) =>
            string.Equals(entry.Program.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase);
        static bool IsInProgramDirectory(Entry child, Entry parent)
        {
            if (!Path.IsPathFullyQualified(child.ExecutablePath)
                || !Path.IsPathFullyQualified(parent.ExecutablePath))
            {
                return false;
            }
            var relative = Path.GetRelativePath(Path.GetDirectoryName(parent.ExecutablePath)!, child.ExecutablePath);
            return !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        // 同一路径的父子进程标记多进程应用的根，防止辅助进程一路归入启动它的外部程序。
        var applicationRoots = new HashSet<int>();
        foreach (var entry in entries)
        {
            var parent = Parent(entry);
            if (parent is not null
                && Path.IsPathFullyQualified(entry.ExecutablePath)
                && string.Equals(entry.ExecutablePath, parent.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                applicationRoots.Add(parent.Program.ProcessId);
            }
        }

        var membership = new Dictionary<int, string>();
        var roots = new Dictionary<string, Entry>();
        foreach (var entry in entries)
        {
            var visited = new HashSet<int>();
            Entry? cursor = entry;
            Entry? packageRoot = null;
            Entry? windowRoot = null;
            // 优先寻找包或有窗口的祖先；Explorer 是启动边界，不能吸收它启动的所有应用。
            while (cursor is not null && visited.Add(cursor.Program.ProcessId))
            {
                if (cursor.Package is not null)
                {
                    packageRoot = cursor;
                    break;
                }
                var parent = Parent(cursor);
                if (parent?.Package is not null)
                {
                    packageRoot = parent;
                    break;
                }
                if (cursor.HasWindow)
                {
                    if (!IsExplorer(cursor) || cursor == entry)
                    {
                        windowRoot = cursor;
                    }
                    break;
                }
                if (IsExplorer(cursor))
                {
                    break;
                }
                if (parent is not null
                    && applicationRoots.Contains(cursor.Program.ProcessId)
                    && !IsInProgramDirectory(cursor, parent))
                {
                    break;
                }
                cursor = parent;
            }

            var root = packageRoot ?? windowRoot ?? entry;
            // 无窗口应用退回同一程序目录内的父子链，使托盘程序及其辅助进程仍能归组。
            if (packageRoot is null && windowRoot is null)
            {
                visited.Clear();
                while (!root.HasWindow && !IsExplorer(root) && visited.Add(root.Program.ProcessId))
                {
                    var parent = Parent(root);
                    if (parent is null || IsExplorer(parent) || !IsInProgramDirectory(root, parent))
                    {
                        break;
                    }
                    root = parent;
                }
            }
            // 包和文件路径分组均限定用户、会话，避免不同登录会话中的同名应用混在一起。
            var scope = $"{root.Program.SessionId}:{root.Program.UserName.ToUpperInvariant()}";
            var key = packageRoot is not null
                ? $"package:{scope}:{root.Package}"
                : root.HasWindow || !Path.IsPathFullyQualified(root.ExecutablePath)
                    ? $"process:{root.Program.ProcessId}"
                    : $"file:{scope}:{root.ExecutablePath.ToUpperInvariant()}";
            membership[entry.Program.ProcessId] = key;
            if (!roots.TryGetValue(key, out var existingRoot) || (root.HasWindow && !existingRoot.HasWindow))
            {
                roots[key] = root;
            }
        }
        return entries.Select(entry =>
        {
            var groupId = membership[entry.Program.ProcessId];
            var root = roots[groupId];
            return entry.Program with
            {
                DisplayName = entry.DisplayName,
                GroupId = groupId,
                GroupName = root.PackageName ?? root.DisplayName,
                GroupProcessName = root.Program.Name
            };
        }).ToArray();
    }

    private static Dictionary<int, int> ReadParents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == new IntPtr(-1))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取进程快照");
        }
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                }
                while (Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private static Dictionary<int, string[]> ReadWindows()
    {
        var windows = new List<(int ProcessId, string Title)>();
        var frames = new List<(int ProcessId, string Title)>();
        var coreWindows = new List<(int ProcessId, string Title)>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }
            var length = GetWindowTextLengthW(window);
            if (length == 0)
            {
                return true;
            }
            var title = new StringBuilder(length + 1);
            if (GetWindowTextW(window, title, title.Capacity) == 0)
            {
                return true;
            }
            GetWindowThreadProcessId(window, out var pid);
            var className = new StringBuilder(128);
            GetClassNameW(window, className, className.Capacity);
            if (className.ToString() == "ApplicationFrameWindow")
            {
                var extendedStyle = GetWindowLongW(window, ExtendedWindowStyle);
                var style = GetWindowLongW(window, WindowStyle);
                if ((extendedStyle & NoActivate) != 0
                    || ((extendedStyle & AppWindow) == 0
                        && (extendedStyle & OverlappedWindow) != OverlappedWindow
                        && (style & Caption) != Caption))
                {
                    return true;
                }
                var appPid = 0;
                EnumChildWindows(window, (child, _) =>
                {
                    GetClassNameW(child, className, className.Capacity);
                    if (className.ToString() != "Windows.UI.Core.CoreWindow")
                    {
                        return true;
                    }
                    GetWindowThreadProcessId(child, out var childPid);
                    appPid = (int)childPid;
                    return false;
                }, IntPtr.Zero);
                frames.Add((appPid, title.ToString()));
            }
            else if (className.ToString() == "Windows.UI.Core.CoreWindow")
            {
                coreWindows.Add(((int)pid, title.ToString()));
            }
            else if ((GetWindowLongW(window, ExtendedWindowStyle) & ToolWindow) == 0)
            {
                windows.Add(((int)pid, title.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return AssociateWindows(windows, frames, coreWindows);
    }

    /// <summary>
    /// 将 UWP 框架窗口标题关联到实际应用进程；缺少子窗口 PID 时按核心窗口标题回退匹配。
    /// </summary>
    internal static Dictionary<int, string[]> AssociateWindows(
        List<(int ProcessId, string Title)> windows,
        IReadOnlyList<(int ProcessId, string Title)> frames,
        IReadOnlyList<(int ProcessId, string Title)> coreWindows)
    {
        foreach (var frame in frames)
        {
            var ownerId = frame.ProcessId != 0
                ? frame.ProcessId
                : coreWindows.FirstOrDefault(core => core.Title == frame.Title).ProcessId;
            if (ownerId != 0)
            {
                windows.Add((ownerId, frame.Title));
            }
        }
        return windows.GroupBy(window => window.ProcessId).ToDictionary(group => group.Key,
            group => group.Select(window => window.Title).ToArray());
    }

    private static string? ReadPackageName(string package)
    {
        try
        {
            uint size = 0;
            if (GetPackagePathByFullName(package, ref size, null) != ErrorInsufficientBuffer)
            {
                return null;
            }
            var path = new StringBuilder(checked((int)size));
            if (GetPackagePathByFullName(package, ref size, path) != 0)
            {
                return null;
            }
            var manifest = XDocument.Load(Path.Combine(path.ToString(), "AppxManifest.xml"));
            var name = manifest.Root?.Elements().FirstOrDefault(node => node.Name.LocalName == "Properties")?
                .Elements().FirstOrDefault(node => node.Name.LocalName == "DisplayName")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }
            if (!name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
            var resource = name["ms-resource:".Length..];
            var identity = manifest.Root?.Elements().FirstOrDefault(node => node.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
            var uri = resource.StartsWith("//", StringComparison.Ordinal)
                ? "ms-resource:" + resource
                : $"ms-resource://{identity}/{(resource.StartsWith('/') ? resource.TrimStart('/') : "Resources/" + resource)}";
            var output = new StringBuilder(1024);
            return SHLoadIndirectString($"@{{{package}? {uri}}}", output, (uint)output.Capacity, IntPtr.Zero) == 0
                ? output.ToString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process,
        uint flags,
        StringBuilder name,
        ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(IntPtr process, ref uint size, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string name, ref uint size, StringBuilder? path);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int size);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string source,
        StringBuilder output,
        uint size,
        IntPtr reserved);
}
