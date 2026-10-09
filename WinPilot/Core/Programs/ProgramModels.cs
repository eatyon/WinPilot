namespace WinPilot.Core.Programs;

public sealed record RunningProgram(int ProcessId, string Name, string UserName, int SessionId);

public sealed record InteractiveUser(string UserName, bool IsCurrentUser);

public sealed record ProgramUsage(IReadOnlyList<int> ProcessIds, string Name, double CpuPercent, long MemoryBytes, double DiskBytesPerSecond, double? GpuPercent);

public sealed record ProgramLaunchRequest(string ProgramPath, string Arguments, string WorkingDirectory, bool RunElevated, bool Silent = false);

public sealed record ProgramTag(string Name, string ProgramPath, string Arguments, string WorkingDirectory, bool RunElevated, bool Silent = false);
