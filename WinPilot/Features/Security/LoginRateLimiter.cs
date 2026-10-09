using System.Collections.Concurrent;

namespace WinPilot.Features.Security;

public sealed class LoginRateLimiter
{
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Attempt> attempts = new(StringComparer.Ordinal);
    public bool IsLocked(string source) => attempts.TryGetValue(source, out var attempt) && attempt.LockedUntil > DateTimeOffset.UtcNow;
    public void Success(string source) => attempts.TryRemove(source, out _);
    public bool Failure(string source)
    {
        while (true)
        {
            if (!attempts.TryGetValue(source, out var current))
            {
                if (attempts.TryAdd(source, new Attempt(1, null))) return false;
                continue;
            }
            var now = DateTimeOffset.UtcNow;
            if (current.LockedUntil > now) return false;
            var lockedNow = current.Failed >= 4;
            var next = lockedNow ? new Attempt(0, now.Add(LockDuration)) : current with { Failed = current.Failed + 1 };
            if (attempts.TryUpdate(source, next, current)) return lockedNow;
        }
    }
    private sealed record Attempt(int Failed, DateTimeOffset? LockedUntil);
}
