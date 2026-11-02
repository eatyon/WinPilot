using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WinPilot.Core.Programs;

namespace WinPilot.Windows;

/// <summary>
/// 实现 Windows 用户进程查询、资源采样、结束和启动，集中处理系统权限与进程保护。
/// </summary>
public sealed class WindowsProgramManager : IProgramManager
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint TokenImpersonate = 0x0004;
    private const int TokenInformationUser = 1;
    private const uint SecurityImpersonation = 2;
    private const uint TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;
    private const int ExtendedWindowStyle = -20;
    private const int ToolWindow = 0x80;
    private const uint WmSysCommand = 0x0112;
    private const nuint ScClose = 0xF060;
    private const uint SmtoBlock = 1;
    private const uint WindowCloseTimeoutMilliseconds = 500;
    private const int ProcessExitTimeoutMilliseconds = 5000;
    private static readonly HashSet<string> CoreProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "smss",
        "dwm",
        "fontdrvhost",
        "Registry",
        "System",
        "Idle"
    };
    private readonly int currentProcessId = Environment.ProcessId;
    private readonly ILogger<WindowsProgramManager> logger;

    public WindowsProgramManager(ILogger<WindowsProgramManager> logger) => this.logger = logger;

    public IReadOnlyList<InteractiveUser> GetInteractiveUsers()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUserName = identity.Name;
        var users = GetWtsInteractiveUsers();
        if (users.Count == 0)
        {
            logger.LogWarning("无法获取 Windows 已登录用户列表，改用当前运行 WinPilot 的用户");
            users = [currentUserName];
        }

        return users
            .Select(userName => new InteractiveUser(userName, string.Equals(userName, currentUserName, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(user => user.IsCurrentUser)
            .ThenBy(user => user.UserName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<RunningProgram> GetRunningPrograms(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new InvalidOperationException("请选择交互式用户");
        }
        var programs = GetEnumeratedPrograms().Where(program => string.Equals(program.UserName, userName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return WindowsProgramCatalog.Describe(programs);
    }

    private IReadOnlyList<RunningProgram> GetEnumeratedPrograms() =>
        Process
            .GetProcesses()
            .Select(TryCreateEnumeratedProgram)
            .Where(program => program is not null)
            .Cast<RunningProgram>()
            .OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(program => program.UserName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(program => program.SessionId)
            .ThenBy(program => program.ProcessId)
            .ToArray();

    /// <summary>
    /// 对选中进程连续采样，仅用两次采样均取得数据的成员计算合计资源占用。
    /// </summary>
    public ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken)
    {
        var processes = GetObservableProcesses(processIds);
        try
        {
            var first = Capture(processes);
            if (first.Processes.Count == 0)
            {
                throw new InvalidOperationException("进程已退出");
            }
            if (cancellationToken.WaitHandle.WaitOne(750))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var second = Capture(processes);
            var usage = CalculateUsage(first, second, Environment.ProcessorCount);
            var usageProcessIds = usage.ProcessIds;
            double? gpuPercent;
            try
            {
                var gpuSample = PdhGpuUsageReader.ReadUsage(usageProcessIds, cancellationToken);
                gpuPercent = gpuSample.UsagePercent;
                logger.LogDebug(
                    "进程 GPU 使用率采样，进程 ID：{ProcessIds}，匹配样本：{MatchingCount}，有效样本：{ValidCount}，显卡数量：{AdapterCount}，使用率：{GpuPercent:F1}%",
                    string.Join(",", usageProcessIds),
                    gpuSample.MatchingItemCount,
                    gpuSample.ValidValueCount,
                    gpuSample.AdapterCount,
                    gpuPercent);
            }
            catch (PdhGpuUsageUnavailableException exception)
            {
                logger.LogDebug(exception, "无法获取 PID {ProcessIds} 的 GPU 使用率", string.Join(",", usageProcessIds));
                gpuPercent = null;
            }
            return usage with { GpuPercent = gpuPercent };
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public void Close(IReadOnlyList<int> processIds, bool isGroup)
    {
        var processes = GetManageableProcesses(processIds);
        try
        {
            // 整组结束先向成员的可见普通窗口发送关闭消息；单个结束跳过窗口处理。
            if (isGroup)
            {
                var selectedProcesses = processes.Where(process => !process.HasExited).ToDictionary(process => process.Id);
                var windows = new List<IntPtr>();
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var processId);
                    if (selectedProcesses.ContainsKey((int)processId)
                        && IsWindowVisible(window)
                        && GetWindowTextLengthW(window) > 0
                        && (GetWindowLongW(window, ExtendedWindowStyle) & ToolWindow) == 0)
                    {
                        windows.Add(window);
                    }
                    return true;
                }, IntPtr.Zero);
                foreach (var window in windows)
                {
                    GetWindowThreadProcessId(window, out var processId);
                    if (selectedProcesses.TryGetValue((int)processId, out var process) && !process.HasExited)
                    {
                        SendMessageTimeoutW(window, WmSysCommand, ScClose, IntPtr.Zero, SmtoBlock, WindowCloseTimeoutMilliseconds, out _);
                    }
                }
            }

            // 两种入口最终都结束选中的进程，不递归扩大到未选中的子进程树。
            var failures = new List<string>();
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: false);
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    if (!HasProcessExited(process))
                    {
                        failures.Add($"PID {process.Id} 结束失败：{exception.Message}");
                    }
                }
            }
            // 所有成员共用退出等待预算，避免组越大等待时间越长。
            var exitWait = Stopwatch.StartNew();
            foreach (var process in processes)
            {
                if (!process.WaitForExit((int)Math.Max(0, ProcessExitTimeoutMilliseconds - exitWait.ElapsedMilliseconds)))
                {
                    failures.Add($"PID {process.Id} 未在 {ProcessExitTimeoutMilliseconds / 1000} 秒内退出");
                }
            }
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", failures));
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public void Launch(ProgramLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProgramPath))
        {
            throw new InvalidOperationException("请输入程序路径");
        }
        request = PrepareLaunchRequest(request);
        if (!File.Exists(request.ProgramPath))
        {
            throw new FileNotFoundException("找不到指定的程序文件");
        }
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) && !Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException("起始目录不存在");
        }
        var launch = CreateLaunchPlan(request);
        // WinPilot 已以管理员权限运行，最高权限启动沿用当前身份，普通启动另取 Explorer 令牌。
        if (request.RunElevated)
        {
            using var process = Process.Start(new ProcessStartInfo(launch.ExecutablePath, launch.Arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = request.WorkingDirectory,
                WindowStyle = launch.HideWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                CreateNoWindow = launch.HideWindow
            }) ?? throw new InvalidOperationException("无法启动程序");
            return;
        }
        StartAsStandardUser(request, launch);
    }

    internal static ProgramLaunchRequest PrepareLaunchRequest(ProgramLaunchRequest request)
    {
        var programPath = Path.GetFullPath(request.ProgramPath);
        return request with
        {
            ProgramPath = programPath,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Path.GetDirectoryName(programPath)!
                : request.WorkingDirectory
        };
    }

    private RunningProgram? TryCreateEnumeratedProgram(Process process)
    {
        try
        {
            if (!TryGetObservableUserName(process, out var userName))
            {
                return null;
            }
            return new RunningProgram(process.Id, GetDisplayName(process), userName, process.SessionId)
            {
                CanEnd = CanEnd(process, userName, out _)
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    private List<Process> GetManageableProcesses(IReadOnlyList<int> processIds) => GetSelectedProcesses(processIds, true);

    private List<Process> GetObservableProcesses(IReadOnlyList<int> processIds) => GetSelectedProcesses(processIds, false);

    private List<Process> GetSelectedProcesses(IReadOnlyList<int> processIds, bool requireManageable)
    {
        if (processIds is null || processIds.Count == 0)
        {
            throw new InvalidOperationException("请选择至少一个进程");
        }
        var processes = new List<Process>(processIds.Count);
        try
        {
            foreach (var processId in processIds.Distinct())
            {
                Process process;
                try
                {
                    process = Process.GetProcessById(processId);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                processes.Add(process);
                if (HasProcessExited(process))
                {
                    continue;
                }
                if (requireManageable)
                {
                    if (!CanEnd(process, GetOwner(process.Id), out var reason) && !HasProcessExited(process))
                    {
                        throw new InvalidOperationException($"PID {processId} 无法结束：{reason}");
                    }
                }
                else if (!TryGetObservableUserName(process, out _) && !HasProcessExited(process))
                {
                    throw new InvalidOperationException($"PID {processId} 无法获取进程信息，进程已退出或受到系统保护");
                }
            }
            if (!requireManageable && processes.Count == 0)
            {
                throw new InvalidOperationException("进程已退出");
            }
            return processes;
        }
        catch
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
            throw;
        }
    }

    private bool TryGetObservableUserName(Process process, out string userName)
    {
        userName = string.Empty;
        var owner = GetOwner(process.Id);
        if (string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }
        userName = owner;
        return true;
    }

    /// <summary>
    /// 检查自身进程、系统进程与账户保护；查询展示和实际结束均使用这一规则。
    /// </summary>
    private bool CanEnd(Process process, string? owner, out string reason)
    {
        reason = string.Empty;
        if (process.Id == currentProcessId)
        {
            reason = "这是 WinPilot 自身进程";
            return false;
        }
        if (process.SessionId == 0)
        {
            reason = "属于 Session 0 非交互式服务会话";
            return false;
        }
        if (CoreProcessNames.Contains(process.ProcessName))
        {
            reason = "这是 Windows 核心系统进程";
            return false;
        }

        if (string.IsNullOrWhiteSpace(owner))
        {
            reason = "无法读取进程所属用户，可能是受保护进程";
            return false;
        }
        if (IsSystemAccount(owner))
        {
            reason = "属于系统服务账户";
            return false;
        }

        return true;
    }

    private static Snapshot Capture(IReadOnlyList<Process> processes)
    {
        var samples = new Dictionary<int, ProcessSnapshot>();
        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }
                process.Refresh();
                var cpuTime = process.TotalProcessorTime;
                var memoryBytes = GetMemoryBytes(process);
                var ioBytes = GetProcessIoCounters(process.Handle, out var counters)
                    ? checked((long)(counters.ReadTransferCount + counters.WriteTransferCount + counters.OtherTransferCount))
                    : 0;
                var name = GetDisplayName(process);
                if (!process.HasExited)
                {
                    samples[process.Id] = new(name, cpuTime, memoryBytes, ioBytes);
                }
            }
            catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                && HasProcessExited(process))
            {
            }
        }
        return new Snapshot(DateTimeOffset.UtcNow, samples);
    }

    internal static ProgramUsage CalculateUsage(Snapshot first, Snapshot second, int processorCount)
    {
        // 仅对两次快照都存在的进程计算增量，避免短命子进程退出导致整组采样失败。
        var survivors = second.Processes.Where(pair => first.Processes.ContainsKey(pair.Key)).ToArray();
        if (survivors.Length == 0)
        {
            throw new InvalidOperationException("进程已退出");
        }
        var elapsedSeconds = Math.Max(0.001, (second.Timestamp - first.Timestamp).TotalSeconds);
        var cpuSeconds = survivors.Sum(pair => Math.Max(0, (pair.Value.CpuTime - first.Processes[pair.Key].CpuTime).TotalSeconds));
        var ioBytes = survivors.Sum(pair => Math.Max(0, pair.Value.IoBytes - first.Processes[pair.Key].IoBytes));
        return new ProgramUsage(
            survivors.Select(pair => pair.Key).ToArray(),
            survivors[0].Value.Name,
            cpuSeconds / elapsedSeconds / Math.Max(1, processorCount) * 100,
            survivors.Sum(pair => pair.Value.MemoryBytes),
            ioBytes / elapsedSeconds,
            null);
    }

    private static bool HasProcessExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static long GetMemoryBytes(Process process)
    {
        var counters = new ProcessMemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        if (GetProcessMemoryInfo(process.Handle, ref counters, counters.Cb)
            && counters.PrivateWorkingSetSize > 0)
        {
            return checked((long)counters.PrivateWorkingSetSize);
        }
        return process.WorkingSet64;
    }

    private static string? GetOwner(int processId)
    {
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
            {
                return null;
            }
            try
            {
                GetTokenInformation(tokenHandle, TokenInformationUser, IntPtr.Zero, 0, out var length);
                var buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (!GetTokenInformation(tokenHandle, TokenInformationUser, buffer, length, out _))
                    {
                        return null;
                    }
                    var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
                    return new SecurityIdentifier(tokenUser.User.Sid).Translate(typeof(NTAccount)).Value;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static bool IsSystemAccount(string account) =>
        account.EndsWith("\\SYSTEM", StringComparison.OrdinalIgnoreCase)
        || account.EndsWith("\\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
        || account.EndsWith("\\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> GetWtsInteractiveUsers()
    {
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessionBuffer, out var sessionCount))
        {
            return users;
        }
        try
        {
            var sessionSize = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < sessionCount; index++)
            {
                var session = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(sessionBuffer, checked(index * sessionSize)));
                if (session.SessionId == 0
                    || session.State is not (WtsConnectState.Active or WtsConnectState.Connected or WtsConnectState.Disconnected))
                {
                    continue;
                }
                var userName = GetWtsSessionValue(session.SessionId, WtsInfoClass.UserName);
                var domainName = GetWtsSessionValue(session.SessionId, WtsInfoClass.DomainName);
                if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(domainName))
                {
                    continue;
                }
                var account = $"{domainName}\\{userName}";
                if (!IsActualWindowsUserAccount(account, userName))
                {
                    continue;
                }
                users.Add(account);
            }
        }
        finally
        {
            WTSFreeMemory(sessionBuffer);
        }

        return users;
    }

    private static string? GetWtsSessionValue(int sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static bool IsActualWindowsUserAccount(string account, string userName) =>
        !IsSystemAccount(account)
        && !userName.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase)
        && !userName.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(userName, "ANONYMOUS LOGON", StringComparison.OrdinalIgnoreCase);

    private static void StartAsStandardUser(ProgramLaunchRequest request, LaunchPlan launch)
    {
        using var explorer = FindCurrentUserExplorer() ?? throw new InvalidOperationException("未检测到当前用户的 Explorer.exe，无法以普通权限启动程序");

        if (!OpenProcessToken(
            explorer.Handle,
            TokenQuery | TokenDuplicate | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId | TokenImpersonate,
            out var explorerToken))
        {
            throw new InvalidOperationException("无法读取当前用户 Explorer.exe 的访问令牌，无法以普通权限启动程序");
        }

        try
        {
            if (!DuplicateTokenEx(
                explorerToken,
                TokenQuery | TokenAssignPrimary | TokenDuplicate | TokenAdjustDefault | TokenAdjustSessionId | TokenImpersonate,
                IntPtr.Zero,
                SecurityImpersonation,
                TokenPrimary,
                out var primaryToken))
            {
                throw new InvalidOperationException("无法创建当前用户的普通权限令牌");
            }

            try
            {
                var startupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfo>(),
                    lpDesktop = "winsta0\\default",
                    dwFlags = launch.HideWindow ? (int)StartfUseShowWindow : 0,
                    wShowWindow = launch.HideWindow ? SwHide : (short)0
                };
                var commandLine = BuildCommandLine(launch.ExecutablePath, launch.Arguments);
                var creationFlags = CreateUnicodeEnvironment | (launch.HideWindow ? CreateNoWindow : 0);
                if (!CreateProcessWithTokenW(
                    primaryToken,
                    0,
                    launch.ExecutablePath,
                    commandLine,
                    creationFlags,
                    IntPtr.Zero,
                    request.WorkingDirectory,
                    ref startupInfo,
                    out var processInformation))
                {
                    throw new InvalidOperationException($"无法以普通权限启动程序，Windows 错误代码：{Marshal.GetLastWin32Error()}");
                }
                CloseHandle(processInformation.hThread);
                CloseHandle(processInformation.hProcess);
            }
            finally
            {
                CloseHandle(primaryToken);
            }
        }
        finally
        {
            CloseHandle(explorerToken);
        }
    }

    private static LaunchPlan CreateLaunchPlan(ProgramLaunchRequest request)
    {
        var extension = Path.GetExtension(request.ProgramPath);
        var scriptPath = QuoteArgument(request.ProgramPath);
        return extension.ToLowerInvariant() switch
        {
            ".exe" => new LaunchPlan(request.ProgramPath, request.Arguments, false),
            ".bat" or ".cmd" => new LaunchPlan(
                Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/d /s /c \"{scriptPath}{AppendArguments(request.Arguments)}\"",
                request.Silent),
            ".vbs" => new LaunchPlan(
                Path.Combine(Environment.SystemDirectory, "wscript.exe"),
                $"//nologo {scriptPath}{AppendArguments(request.Arguments)}",
                true),
            ".py" => new LaunchPlan(FindPythonExecutable(), $"{scriptPath}{AppendArguments(request.Arguments)}", request.Silent),
            _ => throw new InvalidOperationException("不支持此文件类型")
        };
    }

    private static string FindPythonExecutable()
    {
        var searchPaths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in searchPaths)
        {
            foreach (var executableName in new[] {
                "python.exe",
                "python3.exe",
                "py.exe"
})
            {
                var executablePath = Path.Combine(directory.Trim('"'), executableName);
                if (File.Exists(executablePath))
                {
                    return executablePath;
                }
            }
        }

        foreach (var launcherDirectory in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            var launcherPath = Path.Combine(launcherDirectory, "Programs", "Python", "Launcher", "py.exe");
            if (File.Exists(launcherPath))
            {
                return launcherPath;
            }
        }
        throw new InvalidOperationException("未找到可用的 Python 解释器，请安装 Python 并将其加入 PATH 环境变量");
    }

    private static string AppendArguments(string arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments;

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string BuildCommandLine(string programPath, string arguments) =>
        $"{QuoteArgument(programPath)}{AppendArguments(arguments)}";

    private static Process? FindCurrentUserExplorer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUserName = identity.Name;
        var processes = Process.GetProcessesByName("explorer");
        Process? selected = null;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId != 0
                        && string.Equals(GetOwner(process.Id), currentUserName, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = process;
                        break;
                    }
                }
                catch
                {
                }
            }
            return selected;
        }
        finally
        {
            foreach (var process in processes)
            {
                if (process != selected)
                {
                    process.Dispose();
                }
            }
        }
    }

    private static string GetDisplayName(Process process)
    {
        try
        {
            return Path.GetFileName(process.MainModule?.FileName) ?? process.ProcessName;
        }
        catch
        {
            return process.ProcessName + ".exe";
        }
    }

    private sealed record LaunchPlan(string ExecutablePath, string Arguments, bool HideWindow);

    internal sealed record ProcessSnapshot(
        string Name,
        TimeSpan CpuTime,
        long MemoryBytes,
        long IoBytes);

    internal sealed record Snapshot(DateTimeOffset Timestamp, IReadOnlyDictionary<int, ProcessSnapshot> Processes);

    private enum WtsConnectState
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public WtsConnectState State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr window,
        uint message,
        nuint wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out nuint result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        uint impersonationLevel,
        uint tokenType,
        out IntPtr duplicateToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        uint logonFlags,
        string? applicationName,
        string commandLine,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr processHandle,
        ref ProcessMemoryCountersEx2 counters,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr server,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr server,
        int sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
