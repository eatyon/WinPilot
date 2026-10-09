namespace WinPilot.Features.Terminal;

public sealed record TerminalSessionInfo(Guid SessionId, string Shell, string WorkingDirectory, DateTimeOffset CreatedAt);
public sealed record TerminalEvent(string Type, Guid? SessionId, string? Stream, string Data, int? ExitCode = null);
public sealed record ShellCommandResult(string Shell, bool ReceivedOutput);
