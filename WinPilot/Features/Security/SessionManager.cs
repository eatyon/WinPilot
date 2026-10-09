using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace WinPilot.Features.Security;

public sealed class SessionManager : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SessionState> sessions = new(StringComparer.Ordinal);
    private readonly object sessionGate = new();
    private bool disposed;

    public string Create()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (sessionGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var item in sessions)
                if (item.Value.IsExpired && ((ICollection<KeyValuePair<string, SessionState>>)sessions).Remove(item))
                    item.Value.Revoke();
            sessions[id] = new SessionState(Lifetime);
        }
        return id;
    }

    public bool Validate(string? id) => TryValidate(id, out _);

    public bool TryValidate(string? id, out CancellationToken invalidated)
    {
        invalidated = default;
        if (string.IsNullOrWhiteSpace(id) || !sessions.TryGetValue(id, out var state) || !state.TryRefresh())
        {
            if (!string.IsNullOrWhiteSpace(id) && sessions.TryRemove(id, out var removed)) removed.Revoke();
            return false;
        }
        invalidated = state.Invalidated;
        return true;
    }

    public void RevokeAll()
    {
        lock (sessionGate)
            foreach (var id in sessions.Keys)
                if (sessions.TryRemove(id, out var state)) state.Revoke();
    }

    public void Dispose()
    {
        lock (sessionGate)
        {
            if (disposed) return;
            disposed = true;
            RevokeAll();
        }
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

        public void Revoke()
        {
            lock (gate)
            {
                if (revoked) return;
                revoked = true;
            }
            try
            {
                if (!invalidated.IsCancellationRequested) invalidation.Cancel();
            }
            finally { invalidation.Dispose(); }
        }
    }
}
