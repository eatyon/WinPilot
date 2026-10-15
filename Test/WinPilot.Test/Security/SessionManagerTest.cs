using System.Collections;
using System.Reflection;
using WinPilot.Features.Security;

namespace WinPilot.Test;

public sealed class SessionManagerTest
{
    [Theory]
    [InlineData("revoke")]
    [InlineData("dispose")]
    [InlineData("create")]
    [InlineData("validate")]
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
        if (operation is "create" or "validate") Expire(sessions, id);
        var revoking = Task.Run(() =>
        {
            switch (operation)
            {
                case "revoke": sessions.RevokeAll(); break;
                case "dispose": sessions.Dispose(); break;
                case "create": Assert.True(sessions.Validate(sessions.Create())); break;
                case "validate": Assert.False(sessions.Validate(id)); break;
            }
        });
        Task? probing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            probing = Task.Run(() =>
            {
                Assert.False(sessions.Validate(id));
                if (operation == "dispose") Assert.Throws<ObjectDisposedException>(() => sessions.Create());
                else Assert.True(sessions.Validate(sessions.Create()));
            });
            await probing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await revoking.WaitAsync(TimeSpan.FromSeconds(5));
            if (probing is not null) await probing.WaitAsync(TimeSpan.FromSeconds(5));
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
        var first = Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); sessions.RevokeAll(); });
        var second = Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); sessions.Dispose(); });
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
        finally { foreach (var registration in registrations) registration.Dispose(); }
    }

    private static void Expire(SessionManager manager, string id)
    {
        var states = (IDictionary)typeof(SessionManager).GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        var state = states[id]!;
        state.GetType().GetField("expiresAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(state, DateTimeOffset.UtcNow.AddMinutes(-1));
    }
}
