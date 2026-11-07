using System.Collections.Concurrent;
using System.Security.Cryptography;
using WinPilot.Core;

namespace WinPilot.Features.Security;

/// <summary>
/// 管理内存中的登录会话；有效访问会续期，过期或撤销会通过取消令牌通知正在执行的请求。
/// </summary>
public sealed class SessionManager(ILogger<SessionManager>? logger = null, SecurityEventNotifier? securityEvents = null) : IDisposable
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SessionState> sessions = new(StringComparer.Ordinal);
    private readonly object sessionGate = new();
    private bool disposed;

    public string Create(string? sourceIp = null) => CreateCore(null, 0, sourceIp)!;

    internal string? Create(AuthStore auth, long authenticationVersion, string? sourceIp = null) => CreateCore(auth, authenticationVersion, sourceIp);

    private string? CreateCore(AuthStore? auth, long authenticationVersion, string? sourceIp)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        List<SessionState> revoked = [];
        try
        {
            lock (sessionGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                revoked = RemoveSessions(expiredOnly: true);
                // 密码验证期间可能发生修改；版本已变时不能再凭旧验证结果创建会话。
                if (auth is not null && !auth.IsCurrentVersion(authenticationVersion))
                {
                    return null;
                }
                var state = new SessionState(Lifetime, sourceIp, expired => Expire(id, expired));
                sessions[id] = state;
                state.Start();
            }
            return id;
        }
        // 取消回调可能执行外部逻辑，离开会话锁后再触发。
        finally
        {
            EndSessions(revoked);
        }
    }

    public bool Validate(string? id) => TryValidate(id, out _);

    /// <summary>
    /// 验证并续期会话；成功时返回用于中断该会话关联请求的失效令牌。
    /// </summary>
    public bool TryValidate(string? id, out CancellationToken invalidated)
    {
        invalidated = default;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }
        SessionState? revoked = null;
        lock (sessionGate)
        {
            if (sessions.TryGetValue(id, out var state) && state.TryRefresh())
            {
                invalidated = state.Invalidated;
                return true;
            }
            if (sessions.TryRemove(id, out var removed) && removed.TryRevoke(SessionEndReason.TimedOut))
            {
                revoked = removed;
            }
        }
        EndSession(revoked);
        return false;
    }

    /// <summary>
    /// 撤销指定会话，并通知该会话关联的长连接和请求结束。
    /// </summary>
    public void Revoke(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        SessionState? revoked = null;
        lock (sessionGate)
        {
            if (sessions.TryRemove(id, out var state) && state.TryRevoke(SessionEndReason.LoggedOut))
            {
                revoked = state;
            }
        }
        EndSession(revoked);
    }

    /// <summary>
    /// 撤销当前所有会话，并通知关联的长连接和请求结束。
    /// </summary>
    public void RevokeAll()
    {
        List<SessionState> revoked;
        lock (sessionGate)
        {
            revoked = RemoveSessions(expiredOnly: false);
        }
        EndSessions(revoked);
        if (revoked.Count > 0)
        {
            logger?.LogDebug("登录会话已撤销，数量：{Count}", revoked.Count);
        }
    }

    public void Dispose()
    {
        List<SessionState> revoked;
        lock (sessionGate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            revoked = RemoveSessions(expiredOnly: false);
        }
        EndSessions(revoked);
    }

    private List<SessionState> RemoveSessions(bool expiredOnly)
    {
        List<SessionState> revoked = [];
        foreach (var item in sessions)
        {
            if ((!expiredOnly || item.Value.IsExpired)
                && ((ICollection<KeyValuePair<string, SessionState>>)sessions).Remove(item)
                && item.Value.TryRevoke(expiredOnly ? SessionEndReason.TimedOut : null))
            {
                revoked.Add(item.Value);
            }
        }
        return revoked;
    }

    private void EndSessions(List<SessionState> revoked)
    {
        foreach (var state in revoked)
        {
            EndSession(state);
        }
    }

    private void EndSession(SessionState? state)
    {
        if (state is null) return;
        state.CancelAndDispose(logger);
        if (state.EndReason is { } reason)
        {
            securityEvents?.Record(SecurityEventType.SessionInvalidated, state.SourceIp,
                reason: reason == SessionEndReason.TimedOut ? "无操作超时" : "主动退出");
        }
    }

    private enum SessionEndReason { TimedOut, LoggedOut }

    private void Expire(string id, SessionState state)
    {
        SessionState? expired = null;
        lock (sessionGate)
        {
            if (sessions.TryGetValue(id, out var current) && ReferenceEquals(current, state) && state.TryExpire())
            {
                sessions.TryRemove(id, out expired);
            }
        }
        EndSession(expired);
    }

    // 单个会话的续期与撤销共用同一把锁，避免已失效的会话被并发请求重新激活。
    private sealed class SessionState
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource invalidation = new();
        private readonly System.Threading.Timer expiryTimer;
        private readonly TimeSpan lifetime;
        private readonly CancellationToken invalidated;
        private DateTimeOffset expiresAt;
        private bool revoked;

        public SessionState(TimeSpan lifetime, string? sourceIp, Action<SessionState> expire)
        {
            this.lifetime = lifetime;
            SourceIp = sourceIp;
            invalidated = invalidation.Token;
            expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
            expiryTimer = new System.Threading.Timer(_ => expire(this), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public void Start() => expiryTimer.Change(lifetime, Timeout.InfiniteTimeSpan);

        public CancellationToken Invalidated => invalidated;
        public string? SourceIp { get; }
        public SessionEndReason? EndReason { get; private set; }

        public bool IsExpired
        {
            get
            {
                lock (gate)
                {
                    return revoked || invalidated.IsCancellationRequested || expiresAt <= DateTimeOffset.UtcNow;
                }
            }
        }

        public bool TryRefresh()
        {
            lock (gate)
            {
                if (revoked || invalidated.IsCancellationRequested || expiresAt <= DateTimeOffset.UtcNow)
                {
                    return false;
                }
                expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
                expiryTimer.Change(lifetime, Timeout.InfiniteTimeSpan);
                return true;
            }
        }

        public bool TryExpire()
        {
            lock (gate)
            {
                if (revoked) return false;
                var remaining = expiresAt - DateTimeOffset.UtcNow;
                // 已排队的旧回调可能在续期后执行；重新核对期限，不能误删有效会话。
                if (remaining > TimeSpan.Zero && !invalidated.IsCancellationRequested)
                {
                    expiryTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                    return false;
                }
                revoked = true;
                EndReason = SessionEndReason.TimedOut;
                return true;
            }
        }

        public bool TryRevoke(SessionEndReason? reason = null)
        {
            lock (gate)
            {
                if (revoked)
                {
                    return false;
                }
                revoked = true;
                EndReason = reason is not null && expiresAt <= DateTimeOffset.UtcNow ? SessionEndReason.TimedOut : reason;
                return true;
            }
        }

        public void CancelAndDispose(ILogger<SessionManager>? logger)
        {
            expiryTimer.Dispose();
            try
            {
                if (!invalidated.IsCancellationRequested)
                {
                    invalidation.Cancel();
                }
            }
            catch (AggregateException exception)
            {
                logger?.LogWarning(exception, "撤销登录会话时，取消回调失败");
            }
            finally
            {
                invalidation.Dispose();
            }
        }
    }
}
