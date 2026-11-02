using System.Collections.Concurrent;

namespace WinPilot.Features.Security;

/// <summary>
/// 按登录来源记录失败次数和临时锁定状态；记录仅保存在内存中，由登录请求顺带清理。
/// </summary>
public sealed class LoginRateLimiter(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Attempt> attempts = new(StringComparer.Ordinal);
    private long nextCleanupUtcTicks;

    public bool IsLocked(string source) => GetRemainingLockTime(source) > TimeSpan.Zero;

    /// <summary>
    /// 密码重新保存或认证配置失效时，清除所有来源的失败次数与锁定状态。
    /// </summary>
    public void Reset() => attempts.Clear();

    internal TimeSpan GetRemainingLockTime(string source)
    {
        var now = clock.GetUtcNow();
        Cleanup(now);
        return attempts.TryGetValue(source, out var attempt) && attempt.LockedUntil is { } until && until > now
            ? until - now
            : TimeSpan.Zero;
    }

    public void Success(string source)
    {
        Cleanup(clock.GetUtcNow());
        attempts.TryRemove(source, out _);
    }

    /// <summary>
    /// 记录一次失败，仅在本次失败首次触发锁定时返回 true。
    /// </summary>
    public bool Failure(string source)
    {
        while (true)
        {
            var now = clock.GetUtcNow();
            Cleanup(now);
            if (!attempts.TryGetValue(source, out var current))
            {
                if (attempts.TryAdd(source, new Attempt(1, now.Add(FailureLifetime), null)))
                {
                    return false;
                }
                continue;
            }
            // 已锁定的请求不续期；未锁定时，失败记录过期则重新累计。
            if (current.LockedUntil > now)
            {
                return false;
            }
            var failed = current.ExpiresAt <= now ? 1 : current.Failed + 1;
            var lockedNow = failed >= 5;
            var next = lockedNow
                ? new Attempt(0, now.Add(LockDuration), now.Add(LockDuration))
                : new Attempt(failed, now.Add(FailureLifetime), null);
            // 与同一来源的并发请求竞争更新，失败后重新读取，避免丢失计数。
            if (attempts.TryUpdate(source, next, current))
            {
                return lockedNow;
            }
        }
    }

    private void Cleanup(DateTimeOffset now)
    {
        var next = Volatile.Read(ref nextCleanupUtcTicks);
        if (now.UtcTicks < next
            || Interlocked.CompareExchange(ref nextCleanupUtcTicks, now.Add(CleanupInterval).UtcTicks, next) != next)
        {
            return;
        }
        // 按键和值一起删除，避免误删遍历期间被新请求替换的记录。
        foreach (var item in attempts)
        {
            if (item.Value.ExpiresAt <= now)
            {
                ((ICollection<KeyValuePair<string, Attempt>>)attempts).Remove(item);
            }
        }
    }

    private sealed class Attempt(int failed, DateTimeOffset expiresAt, DateTimeOffset? lockedUntil)
    {
        internal int Failed { get; } = failed;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal DateTimeOffset? LockedUntil { get; } = lockedUntil;
    }
}
