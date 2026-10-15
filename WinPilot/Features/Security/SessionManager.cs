using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace WinPilot.Features.Security;

public sealed class SessionManager(ILogger<SessionManager>? logger = null) : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SessionState> sessions = new(StringComparer.Ordinal);
    private readonly object sessionGate = new();
    private bool disposed;

    public string Create() => CreateCore(null, 0)!;

    internal string? Create(AuthStore auth, long authenticationVersion) => CreateCore(auth, authenticationVersion);

    private string? CreateCore(AuthStore? auth, long authenticationVersion)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        List<SessionState> revoked = [];
        try
        {
            lock (sessionGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                revoked = RemoveSessions(expiredOnly: true);
                if (auth is not null && !auth.IsCurrentVersion(authenticationVersion)) return null;
                sessions[id] = new SessionState(Lifetime);
            }
            return id;
        }
        finally { CancelSessions(revoked); }
    }

    public bool Validate(string? id) => TryValidate(id, out _);

    public bool TryValidate(string? id, out CancellationToken invalidated)
    {
        invalidated = default;
        if (string.IsNullOrWhiteSpace(id)) return false;
        SessionState? revoked = null;
        lock (sessionGate)
        {
            if (sessions.TryGetValue(id, out var state) && state.TryRefresh())
            {
                invalidated = state.Invalidated;
                return true;
            }
            if (sessions.TryRemove(id, out var removed) && removed.TryRevoke()) revoked = removed;
        }
        revoked?.CancelAndDispose(logger);
        return false;
    }

    public void RevokeAll()
    {
        List<SessionState> revoked;
        lock (sessionGate) revoked = RemoveSessions(expiredOnly: false);
        CancelSessions(revoked);
    }

    public void Dispose()
    {
        List<SessionState> revoked;
        lock (sessionGate)
        {
            if (disposed) return;
            disposed = true;
            revoked = RemoveSessions(expiredOnly: false);
        }
        CancelSessions(revoked);
    }

    private List<SessionState> RemoveSessions(bool expiredOnly)
    {
        List<SessionState> revoked = [];
        foreach (var item in sessions)
            if ((!expiredOnly || item.Value.IsExpired) &&
                ((ICollection<KeyValuePair<string, SessionState>>)sessions).Remove(item) && item.Value.TryRevoke())
                revoked.Add(item.Value);
        return revoked;
    }

    private void CancelSessions(List<SessionState> revoked)
    {
        foreach (var state in revoked) state.CancelAndDispose(logger);
    }

    private sealed class SessionState
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource invalidation = new();
        private readonly TimeSpan lifetime;
        private readonly CancellationToken invalidated;
        private DateTimeOffset expiresAt;
        private bool revoked;

        public SessionState(TimeSpan lifetime)
        {
            this.lifetime = lifetime;
            invalidated = invalidation.Token;
            expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
            invalidation.CancelAfter(lifetime);
        }

        public CancellationToken Invalidated => invalidated;
        public bool IsExpired { get { lock (gate) return revoked || invalidated.IsCancellationRequested || expiresAt <= DateTimeOffset.UtcNow; } }

        public bool TryRefresh()
        {
            lock (gate)
            {
                if (revoked || invalidated.IsCancellationRequested || expiresAt <= DateTimeOffset.UtcNow) return false;
                expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
                invalidation.CancelAfter(lifetime);
                return true;
            }
        }

        public bool TryRevoke()
        {
            lock (gate)
            {
                if (revoked) return false;
                revoked = true;
                return true;
            }
        }

        public void CancelAndDispose(ILogger<SessionManager>? logger)
        {
            try
            {
                if (!invalidated.IsCancellationRequested) invalidation.Cancel();
            }
            catch (AggregateException exception) { logger?.LogWarning(exception, "撤销登录会话时，取消回调失败"); }
            finally { invalidation.Dispose(); }
        }
    }
}
