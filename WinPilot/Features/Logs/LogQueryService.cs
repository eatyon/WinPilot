using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WinPilot.Core;

namespace WinPilot.Features.Logs;

public sealed class LogQueryService(PortableDataPaths paths)
{
    private const int MaximumPageSize = 500;
    private const int MaximumOffset = 10_000;
    private static readonly Regex HeaderPattern = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\] \[(?<level>[A-Z]{3})\] (?<source>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<LogDate> GetAvailableDates()
    {
        try
        {
            return Directory.EnumerateFiles(paths.LogsDirectory, "winpilot*.log")
                .Select(path => TryGetDate(path, out var date) ? new LogDate(date) : null)
                    .OfType<LogDate>()
                .OrderByDescending(date => date.Value)
                .ToArray();
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    public LogPage Query(LogQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        if (query.Snapshot is < 0) throw new ArgumentOutOfRangeException(nameof(query.Snapshot));
        if (query.Offset > MaximumOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(query.Offset), $"最多可加载最近 {MaximumOffset} 条历史日志");
        }

        var pageSize = Math.Clamp(query.Limit, 1, MaximumPageSize);
        var logFile = Path.Combine(paths.LogsDirectory, $"winpilot{query.Date:yyyyMMdd}.log");
        if (query.Snapshot == 0) return new LogPage([], false);

        var retainedCount = checked(query.Offset + pageSize);
        var matchingCount = 0;
        var readCount = 0;
        var retainedEntries = new Queue<LogEntry>(retainedCount);
        try
        {
            foreach (var entry in ReadEntries(logFile, cancellationToken, query.Snapshot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                readCount++;
                if (!Matches(entry, query)) continue;

                matchingCount++;
                retainedEntries.Enqueue(entry with { Id = readCount });
                if (retainedEntries.Count > retainedCount) retainedEntries.Dequeue();
            }
        }
        catch (FileNotFoundException) { return new LogPage([], false); }
        catch (DirectoryNotFoundException) { return new LogPage([], false); }

        var entries = retainedEntries.Reverse().Skip(query.Offset).Take(pageSize).ToArray();
        return new LogPage(entries, matchingCount > query.Offset + entries.Length && query.Offset + entries.Length <= MaximumOffset) { Snapshot = readCount };
    }

    private static IEnumerable<LogEntry> ReadEntries(string logFile, CancellationToken cancellationToken, int? maximumEntries)
    {
        using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? header;
        var remainingEntries = maximumEntries ?? int.MaxValue;
        while (remainingEntries > 0 && (header = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = HeaderPattern.Match(header);
            if (!match.Success) continue;

            var bodyLines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null && line.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bodyLines.Add(line);
            }
            if (line is null) yield break;
            if (!TimeOnly.TryParseExact(match.Groups["time"].Value, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;

            var message = bodyLines.Count > 0 ? bodyLines[0] : string.Empty;
            var details = bodyLines.Count > 1 ? string.Join(Environment.NewLine, bodyLines.Skip(1)) : null;
            remainingEntries--;
            yield return new LogEntry(time, match.Groups["level"].Value, match.Groups["source"].Value, message, details);
        }
    }

    private static bool Matches(LogEntry entry, LogQuery query)
    {
        if (query.Levels is not null && !query.Levels.Contains(entry.Level)) return false;

        return string.IsNullOrWhiteSpace(query.Keyword)
            || entry.Source.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase)
            || entry.Details?.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryGetDate(string filePath, out DateOnly date)
    {
        date = default;
        var name = Path.GetFileNameWithoutExtension(filePath);
        return name.Length == 16 && name.StartsWith("winpilot", StringComparison.Ordinal)
            && DateOnly.TryParseExact(name[8..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
