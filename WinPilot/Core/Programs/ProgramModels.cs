namespace WinPilot.Core.Programs;

/// <summary>
/// 一个实际进程的查询快照；分组与窗口信息用于展示，不替代进程身份。
/// </summary>
public sealed record RunningProgram(
    int ProcessId,
    string Name,
    string UserName,
    int SessionId)
{
    public string DisplayName { get; init; } = Name;
    /// <summary>
    /// 前端聚合进程时使用的组标识，未归组时以 PID 区分。
    /// </summary>
    public string GroupId { get; init; } = ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string GroupName { get; init; } = Name;
    public string GroupProcessName { get; init; } = Name;
    public IReadOnlyList<string> WindowTitles { get; init; } = [];
    /// <summary>
    /// 查询时是否允许结束该进程；实际结束仍由后端重新检查。
    /// </summary>
    public bool CanEnd { get; init; }
}

public sealed record InteractiveUser(string UserName, bool IsCurrentUser);

/// <summary>
/// 选中进程的合计占用；内存单位为字节，磁盘为字节每秒，无法取得 GPU 数据时为 null。
/// </summary>
public sealed record ProgramUsage(
    IReadOnlyList<int> ProcessIds,
    string Name,
    double CpuPercent,
    long MemoryBytes,
    double DiskBytesPerSecond,
    double? GpuPercent);

public sealed record ProgramLaunchRequest(
    string ProgramPath,
    string Arguments,
    string WorkingDirectory,
    bool RunElevated,
    bool Silent = false);

public sealed record ProgramTag(
    string Name,
    string ProgramPath,
    string Arguments,
    string WorkingDirectory,
    bool RunElevated,
    bool Silent = false);
