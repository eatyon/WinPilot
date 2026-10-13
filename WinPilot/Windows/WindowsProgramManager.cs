using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WinPilot.Core.Programs;

namespace WinPilot.Windows;

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
    private static readonly HashSet<string> CoreProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "services", "lsass", "smss", "dwm", "fontdrvhost", "Registry", "System", "Idle"
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
        if (string.IsNullOrWhiteSpace(userName)) throw new InvalidOperationException("请选择交互式用户");
        return GetEnumeratedPrograms().Where(program => string.Equals(program.UserName, userName, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private IReadOnlyList<RunningProgram> GetEnumeratedPrograms() => Process.GetProcesses()
        .Select(TryCreateEnumeratedProgram)
        .Where(program => program is not null)
        .Cast<RunningProgram>()
        .OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(program => program.UserName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(program => program.SessionId)
        .ThenBy(program => program.ProcessId)
        .ToArray();

    public ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken)
    {
        var processes = GetObservableProcesses(processIds);
        try
        {
            var first = Capture(processes);
            if (cancellationToken.WaitHandle.WaitOne(750)) cancellationToken.ThrowIfCancellationRequested();
            var second = Capture(processes);
            var elapsedSeconds = Math.Max(0.001, (second.Timestamp - first.Timestamp).TotalSeconds);
            var processorCount = Math.Max(1, Environment.ProcessorCount);
            var cpuPercent = Math.Max(0, (second.CpuTime - first.CpuTime).TotalSeconds / elapsedSeconds / processorCount * 100);
            var diskBytesPerSecond = Math.Max(0, (second.IoBytes - first.IoBytes) / elapsedSeconds);
            var usageProcessIds = processes.Select(process => process.Id).ToArray();
            double? gpuPercent;
            try
            {
                var gpuSample = PdhGpuUsageReader.ReadUsage(usageProcessIds, cancellationToken);
                gpuPercent = gpuSample.UsagePercent;
                logger.LogDebug("进程 GPU 使用率采样，进程 ID：{ProcessIds}，匹配样本：{MatchingCount}，有效样本：{ValidCount}，显卡数量：{AdapterCount}，使用率：{GpuPercent:F1}%", string.Join(",", usageProcessIds), gpuSample.MatchingItemCount, gpuSample.ValidValueCount, gpuSample.AdapterCount, gpuPercent);
            }
            catch (PdhGpuUsageUnavailableException exception)
            {
                logger.LogDebug(exception, "无法获取 PID {ProcessIds} 的 GPU 使用率", string.Join(",", usageProcessIds));
                gpuPercent = null;
            }
            return new ProgramUsage(usageProcessIds, GetDisplayName(processes[0]), cpuPercent, second.MemoryBytes, diskBytesPerSecond, gpuPercent);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public void Close(IReadOnlyList<int> processIds, bool force)
    {
        var processes = GetManageableProcesses(processIds);
        try
        {
            if (force)
            {
                foreach (var process in processes)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: false);
                }
                foreach (var process in processes) process.WaitForExit(5000);
                return;
            }

            var failures = new List<string>();
            foreach (var process in processes)
            {
                if (process.HasExited) continue;
                if (process.MainWindowHandle == IntPtr.Zero) { failures.Add($"PID {process.Id} 没有可正常关闭的主窗口"); continue; }
                if (!process.CloseMainWindow()) { failures.Add($"PID {process.Id} 拒绝了关闭请求"); continue; }
                if (!process.WaitForExit(5000)) failures.Add($"PID {process.Id} 未在 5 秒内正常退出");
            }
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("；", failures));
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public void Launch(ProgramLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProgramPath)) throw new InvalidOperationException("请输入程序路径");
        if (!File.Exists(request.ProgramPath)) throw new FileNotFoundException("找不到指定的程序文件");
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) && !Directory.Exists(request.WorkingDirectory)) throw new DirectoryNotFoundException("起始目录不存在");
        var launch = CreateLaunchPlan(request);
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

    private RunningProgram? TryCreateEnumeratedProgram(Process process)
    {
        try
        {
            if (!TryGetObservableUserName(process, out var userName)) return null;
            return new RunningProgram(process.Id, GetDisplayName(process), userName, process.SessionId);
        }
        catch { return null; }
        finally { process.Dispose(); }
    }

    private List<Process> GetManageableProcesses(IReadOnlyList<int> processIds) => GetSelectedProcesses(processIds, true);

    private List<Process> GetObservableProcesses(IReadOnlyList<int> processIds) => GetSelectedProcesses(processIds, false);

    private List<Process> GetSelectedProcesses(IReadOnlyList<int> processIds, bool requireManageable)
    {
        if (processIds is null || processIds.Count == 0) throw new InvalidOperationException("请选择至少一个进程");
        var processes = new List<Process>(processIds.Count);
        try
        {
            foreach (var processId in processIds.Distinct())
            {
                Process process;
                try { process = Process.GetProcessById(processId); }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException(requireManageable
                        ? $"PID {processId} 已退出，无法关闭进程"
                        : $"PID {processId} 已退出，无法查询占用情况");
                }
                processes.Add(process);
                if (requireManageable)
                {
                    if (!TryGetManageableUserName(process, out _, out var reason))
                        throw new InvalidOperationException($"PID {processId} 无法关闭：{reason}");
                }
                else if (!TryGetObservableUserName(process, out _))
                    throw new InvalidOperationException($"PID {processId} 无法获取进程信息，进程已退出或受到系统保护");
            }
            return processes;
        }
        catch
        {
            foreach (var process in processes) process.Dispose();
            throw;
        }
    }

    private bool TryGetObservableUserName(Process process, out string userName)
    {
        userName = string.Empty;
        if (process.Id == currentProcessId) return false;
        var owner = GetOwner(process.Id);
        if (string.IsNullOrWhiteSpace(owner)) return false;
        userName = owner;
        return true;
    }

    private bool TryGetManageableUserName(Process process, out string userName, out string reason)
    {
        userName = string.Empty;
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

        var owner = GetOwner(process.Id);
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

        userName = owner;
        return true;
    }

    private static Snapshot Capture(IReadOnlyList<Process> processes)
    {
        var cpuTime = TimeSpan.Zero;
        long memoryBytes = 0;
        long ioBytes = 0;
        foreach (var process in processes)
        {
            if (process.HasExited) continue;
            process.Refresh();
            cpuTime += process.TotalProcessorTime;
            memoryBytes += GetMemoryBytes(process);
            if (GetProcessIoCounters(process.Handle, out var counters)) ioBytes += checked((long)(counters.ReadTransferCount + counters.WriteTransferCount + counters.OtherTransferCount));
        }
        return new Snapshot(DateTimeOffset.UtcNow, cpuTime, memoryBytes, ioBytes);
    }

    private static long GetMemoryBytes(Process process)
    {
        var counters = new ProcessMemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        if (GetProcessMemoryInfo(process.Handle, ref counters, counters.Cb) && counters.PrivateWorkingSetSize > 0)
        {
            return checked((long)counters.PrivateWorkingSetSize);
        }
        return process.WorkingSet64;
    }

    private static string? GetOwner(int processId)
    {
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle)) return null;
            try
            {
                GetTokenInformation(tokenHandle, TokenInformationUser, IntPtr.Zero, 0, out var length);
                var buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (!GetTokenInformation(tokenHandle, TokenInformationUser, buffer, length, out _)) return null;
                    var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
                    return new SecurityIdentifier(tokenUser.User.Sid).Translate(typeof(NTAccount)).Value;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(tokenHandle); }
        }
        finally { CloseHandle(processHandle); }
    }

    private static bool IsSystemAccount(string account) => account.EndsWith("\\SYSTEM", StringComparison.OrdinalIgnoreCase)
        || account.EndsWith("\\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) || account.EndsWith("\\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> GetWtsInteractiveUsers()
    {
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessionBuffer, out var sessionCount)) return users;

        try
        {
            var sessionSize = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < sessionCount; index++)
            {
                var session = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(sessionBuffer, checked(index * sessionSize)));
                if (session.SessionId == 0 || session.State is not (WtsConnectState.Active or WtsConnectState.Connected or WtsConnectState.Disconnected)) continue;

                var userName = GetWtsSessionValue(session.SessionId, WtsInfoClass.UserName);
                var domainName = GetWtsSessionValue(session.SessionId, WtsInfoClass.DomainName);
                if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(domainName)) continue;

                var account = $"{domainName}\\{userName}";
                if (!IsActualWindowsUserAccount(account, userName)) continue;
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
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    private static bool IsActualWindowsUserAccount(string account, string userName) => !IsSystemAccount(account)
        && !userName.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase)
        && !userName.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(userName, "ANONYMOUS LOGON", StringComparison.OrdinalIgnoreCase);

    private static void StartAsStandardUser(ProgramLaunchRequest request, LaunchPlan launch)
    {
        using var explorer = FindCurrentUserExplorer() ?? throw new InvalidOperationException("未检测到当前用户的 Explorer.exe，无法以普通权限启动程序");

        if (!OpenProcessToken(explorer.Handle, TokenQuery | TokenDuplicate | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId | TokenImpersonate, out var explorerToken))
        {
            throw new InvalidOperationException("无法读取当前用户 Explorer.exe 的访问令牌，无法以普通权限启动程序");
        }

        try
        {
            if (!DuplicateTokenEx(explorerToken, TokenQuery | TokenAssignPrimary | TokenDuplicate | TokenAdjustDefault | TokenAdjustSessionId | TokenImpersonate, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primaryToken))
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
                if (!CreateProcessWithTokenW(primaryToken, 0, launch.ExecutablePath, commandLine, creationFlags, IntPtr.Zero, request.WorkingDirectory, ref startupInfo, out var processInformation))
                {
                    throw new InvalidOperationException($"无法以普通权限启动程序，Windows 错误代码：{Marshal.GetLastWin32Error()}");
                }
                CloseHandle(processInformation.hThread);
                CloseHandle(processInformation.hProcess);
            }
            finally { CloseHandle(primaryToken); }
        }
        finally { CloseHandle(explorerToken); }
    }

    private static LaunchPlan CreateLaunchPlan(ProgramLaunchRequest request)
    {
        var extension = Path.GetExtension(request.ProgramPath);
        var scriptPath = QuoteArgument(request.ProgramPath);
        return extension.ToLowerInvariant() switch
        {
            ".exe" => new LaunchPlan(request.ProgramPath, request.Arguments, false),
            ".bat" or ".cmd" => new LaunchPlan(Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"{scriptPath}{AppendArguments(request.Arguments)}\"", request.Silent),
            ".vbs" => new LaunchPlan(Path.Combine(Environment.SystemDirectory, "wscript.exe"), $"//nologo {scriptPath}{AppendArguments(request.Arguments)}", true),
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
            foreach (var executableName in new[] { "python.exe", "python3.exe", "py.exe" })
            {
                var executablePath = Path.Combine(directory.Trim('"'), executableName);
                if (File.Exists(executablePath)) return executablePath;
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
            if (File.Exists(launcherPath)) return launcherPath;
        }
        throw new InvalidOperationException("未找到可用的 Python 解释器，请安装 Python 并将其加入 PATH 环境变量");
    }

    private static string AppendArguments(string arguments) => string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments;
    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string BuildCommandLine(string programPath, string arguments) => $"{QuoteArgument(programPath)}{AppendArguments(arguments)}";

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
                    if (process.SessionId != 0 && string.Equals(GetOwner(process.Id), currentUserName, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = process;
                        break;
                    }
                }
                catch { }
            }
            return selected;
        }
        finally
        {
            foreach (var process in processes)
                if (process != selected) process.Dispose();
        }
    }

    private static string GetDisplayName(Process process) { try { return Path.GetFileName(process.MainModule?.FileName) ?? process.ProcessName; } catch { return process.ProcessName + ".exe"; } }
    private sealed record LaunchPlan(string ExecutablePath, string Arguments, bool HideWindow);
    private sealed record Snapshot(DateTimeOffset Timestamp, TimeSpan CpuTime, long MemoryBytes, long IoBytes);
    private enum WtsConnectState { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }
    private enum WtsInfoClass { UserName = 5, DomainName = 7 }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessMemoryCountersEx2 { public uint Cb; public uint PageFaultCount; public nuint PeakWorkingSetSize; public nuint WorkingSetSize; public nuint QuotaPeakPagedPoolUsage; public nuint QuotaPagedPoolUsage; public nuint QuotaPeakNonPagedPoolUsage; public nuint QuotaNonPagedPoolUsage; public nuint PagefileUsage; public nuint PeakPagefileUsage; public nuint PrivateUsage; public nuint PrivateWorkingSetSize; public ulong SharedCommitUsage; }
    [StructLayout(LayoutKind.Sequential)] private struct WtsSessionInfo { public int SessionId; public IntPtr WinStationName; public WtsConnectState State; }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenUser { public SidAndAttributes User; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, uint impersonationLevel, uint tokenType, out IntPtr duplicateToken);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? applicationName, string commandLine, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetProcessMemoryInfo(IntPtr processHandle, ref ProcessMemoryCountersEx2 counters, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, WtsInfoClass infoClass, out IntPtr buffer, out int bytesReturned);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
}
