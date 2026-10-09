namespace WinPilot.Features.Logs;

public sealed record LogDate(DateOnly Value);

public sealed record LogQuery(DateOnly Date, IReadOnlySet<string>? Levels, string? Keyword, int Offset, int Limit, int? Snapshot = null);

public sealed record LogPage(IReadOnlyList<LogEntry> Entries, bool HasMore)
{
    public int Snapshot { get; init; }
}

public sealed record LogEntry(TimeOnly Time, string Level, string Source, string Message, string? Details)
{
    public int Id { get; init; }
}
