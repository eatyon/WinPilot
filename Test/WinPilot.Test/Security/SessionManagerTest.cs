using System.Collections;
using System.Reflection;
using WinPilot.Features.Security;

namespace WinPilot.Test;

public sealed class SessionManagerTest
{
    [Fact]
    public async Task ExpirationRemovesIdleSessionsAndDisposesTheirResourcesWithoutAnotherRequest()
    {
        using var sessions = new SessionManager();
        var id = sessions.Create();
        var other = sessions.Create();
        Assert.True(sessions.TryValidate(id, out var token));
        var state = States(sessions)[id]!;
        var timer = (System.Threading.Timer)state.GetType().GetField("expiryTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)!;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        using var registration = token.Register(() =>
        {
            Interlocked.Increment(ref callbacks);
            cancelled.TrySetResult();
        });

        Expire(sessions, id);
        timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(States(sessions).Contains(id));
        Assert.True(sessions.Validate(other));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { _ = token.WaitHandle; return false; }
            catch (ObjectDisposedException) { return true; }
        }, TimeSpan.FromSeconds(5)));
        Assert.False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        sessions.Revoke(id);
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public void QueuedExpirationCannotRemoveARefreshedSession()
    {
        using var sessions = new SessionManager();
        var id = sessions.Create();
        var state = States(sessions)[id]!;
        Assert.True(sessions.TryValidate(id, out var token));

        typeof(SessionManager).GetMethod("Expire", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(sessions, [id, state]);

        Assert.Same(state, States(sessions)[id]);
        Assert.False(token.IsCancellationRequested);
        Assert.True(sessions.Validate(id));
    }

    [Fact]
    public async Task ConcurrentExpirationExitAndDisposalReleaseTheSessionOnce()
    {
        using var sessions = new SessionManager();
        var id = sessions.Create();
        Assert.True(sessions.TryValidate(id, out var token));
        var state = States(sessions)[id]!;
        Expire(sessions, id);
        var callbacks = 0;
        using var registration = token.Register(() => Interlocked.Increment(ref callbacks));
        using var start = new Barrier(4);
        Action[] operations = [
            () => typeof(SessionManager).GetMethod("Expire", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(sessions, [id, state]),
            () => sessions.Revoke(id),
            sessions.Dispose,
        ];
        var tasks = operations.Select(operation => Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            operation();
        })).ToArray();

        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(States(sessions).Contains(id));
        Assert.Equal(1, callbacks);
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
    }

    [Fact]
    public void RevokeInvalidatesOnlyTheSelectedSession()
    {
        using var sessions = new SessionManager();
        var current = sessions.Create();
        var other = sessions.Create();
        Assert.True(sessions.TryValidate(current, out var invalidated));
        Assert.True(sessions.TryValidate(other, out var otherInvalidated));

        sessions.Revoke(current);
        sessions.Revoke(current);
        sessions.Revoke(null);

        Assert.True(invalidated.IsCancellationRequested);
        Assert.False(sessions.Validate(current));
        Assert.True(sessions.Validate(other));
        Assert.False(otherInvalidated.IsCancellationRequested);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("revoke-current")]
    [InlineData("dispose")]
    [InlineData("create")]
    [InlineData("validate")]
    [InlineData("expire")]
    public async Task RevocationCallbacksDoNotHoldSessionCreationOrValidationLock(string operation)
    {
        using var sessions = new SessionManager();
        var id = sessions.Create();
        Assert.True(sessions.TryValidate(id, out var token));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var callbacks = 0;
        using var registration = token.Register(() =>
        {
            Interlocked.Increment(ref callbacks);
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        if (operation is "create" or "validate" or "expire")
        {
            Expire(sessions, id);
        }
        var revoking = Task.Run(() =>
                {
                    switch (operation)
                    {
                        case "revoke":
                            sessions.RevokeAll();
                            break;
                        case "revoke-current":
                            sessions.Revoke(id);
                            break;
                        case "dispose":
                            sessions.Dispose();
                            break;
                        case "create":
                            Assert.True(sessions.Validate(sessions.Create()));
                            break;
                        case "validate":
                            Assert.False(sessions.Validate(id));
                            break;
                        case "expire":
                            typeof(SessionManager).GetMethod("Expire", BindingFlags.NonPublic | BindingFlags.Instance)!
                                .Invoke(sessions, [id, States(sessions)[id]!]);
                            break;
                    }
                });
        Task? probing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            probing = Task.Run(() =>
            {
                Assert.False(sessions.Validate(id));
                if (operation == "dispose")
                {
                    Assert.Throws<ObjectDisposedException>(() => sessions.Create());
                }
                else
                {
                    Assert.True(sessions.Validate(sessions.Create()));
                }
            });
            await probing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await revoking.WaitAsync(TimeSpan.FromSeconds(5));
            if (probing is not null)
            {
                await probing.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Assert.Equal(1, callbacks);
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
    }

    [Fact]
    public async Task ConcurrentRevocationAndDisposalOwnEachSessionOnce()
    {
        using var sessions = new SessionManager();
        var tokens = Enumerable.Range(0, 8).Select(_ =>
        {
            Assert.True(sessions.TryValidate(sessions.Create(), out var token));
            return token;
        }).ToArray();
        var counts = new int[tokens.Length];
        var registrations = tokens.Select((token, index) => token.Register(() => Interlocked.Increment(ref counts[index]))).ToArray();
        using var start = new Barrier(3);
        var first = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            sessions.RevokeAll();
        });
        var second = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            sessions.Dispose();
        });
        try
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            sessions.Dispose();
            sessions.RevokeAll();
            Assert.All(counts, count => Assert.Equal(1, count));
            Assert.All(tokens, token => Assert.Throws<ObjectDisposedException>(() => token.WaitHandle));
            Assert.Throws<ObjectDisposedException>(() => sessions.Create());
        }
        finally
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }
    }

    private static void Expire(SessionManager manager, string id)
    {
        var state = States(manager)[id]!;
        state.GetType().GetField("expiresAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(state, DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    private static IDictionary States(SessionManager manager) =>
        (IDictionary)typeof(SessionManager).GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
}
