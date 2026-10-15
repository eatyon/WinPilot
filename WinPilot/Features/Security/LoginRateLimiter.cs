using System.Collections.Concurrent;

namespace WinPilot.Features.Security;

public sealed class LoginRateLimiter(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Attempt> attempts = new(StringComparer.Ordinal);
    private long nextCleanupUtcTicks;
    public bool IsLocked(string source)
    {
        var now = clock.GetUtcNow();
        Cleanup(now);
        return attempts.TryGetValue(source, out var attempt) && attempt.LockedUntil > now;
    }
    public void Success(string source)
    {
        Cleanup(clock.GetUtcNow());
        attempts.TryRemove(source, out _);
    }
    public bool Failure(string source)
    {
        while (true)
        {
            var now = clock.GetUtcNow();
            Cleanup(now);
            if (!attempts.TryGetValue(source, out var current))
            {
                if (attempts.TryAdd(source, new Attempt(1, now.Add(FailureLifetime), null))) return false;
                continue;
            }
            if (current.LockedUntil > now) return false;
            var failed = current.ExpiresAt <= now ? 1 : current.Failed + 1;
            var lockedNow = failed >= 5;
            var next = lockedNow ? new Attempt(0, now.Add(LockDuration), now.Add(LockDuration))
                : new Attempt(failed, now.Add(FailureLifetime), null);
            if (attempts.TryUpdate(source, next, current)) return lockedNow;
        }
    }
    private void Cleanup(DateTimeOffset now)
    {
        var next = Volatile.Read(ref nextCleanupUtcTicks);
        if (now.UtcTicks < next || Interlocked.CompareExchange(ref nextCleanupUtcTicks, now.Add(CleanupInterval).UtcTicks, next) != next) return;
        foreach (var item in attempts)
            if (item.Value.ExpiresAt <= now)
                ((ICollection<KeyValuePair<string, Attempt>>)attempts).Remove(item);
    }
    private sealed class Attempt(int failed, DateTimeOffset expiresAt, DateTimeOffset? lockedUntil)
    {
        internal int Failed { get; } = failed;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal DateTimeOffset? LockedUntil { get; } = lockedUntil;
    }
}
