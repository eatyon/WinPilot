using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;

namespace WinPilot.Test;

public sealed class SafeModeConcurrencyTest
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SupersededProtectionCannotDisposeActiveCancellationOrCloseNewTerminals(bool authentication, bool reopen)
    {
        await using var fixture = new TaskServiceFixture();
        var safeMode = new SafeModeService(fixture.Paths, fixture.Terminals, NullLogger<SafeModeService>.Instance);
        var previousProcess = AddTerminal(fixture.Terminals);
        var token = safeMode.RestrictedOperationsCancellation;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var callback = token.Register(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var protection = authentication ? safeMode.EnableForAuthenticationFailure() : null;
        var enabling = Task.Run(() =>
        {
            if (authentication) protection!();
            else safeMode.SetEnabled(true);
        });
        Task? closing = null;
        ProcessDouble? currentProcess = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            closing = Task.Run(() => safeMode.SetEnabled(false));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(token.WaitHandle);
            if (reopen) safeMode.SetEnabled(true);
            var currentToken = safeMode.RestrictedOperationsCancellation;
            currentProcess = AddTerminal(fixture.Terminals);
            await using (var watching = safeMode.WatchAsync(CancellationToken.None).GetAsyncEnumerator())
            {
                Assert.True(await watching.MoveNextAsync());
                Assert.Equal(reopen, watching.Current);
            }
            release.Set();
            await enabling.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(reopen, safeMode.Enabled);
            Assert.Equal(reopen, WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).SafeModeEnabled);
            Assert.Equal(reopen, currentToken.IsCancellationRequested);
            Assert.Equal(1, previousProcess.Kills);
            Assert.Equal(0, currentProcess.Kills);
            Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(enabling, closing ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            safeMode.SetEnabled(false);
            CurrentSource(safeMode).Dispose();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommittedStateIsPublishedBeforeCancellationCallbacksFinish(bool authentication, bool reopen)
    {
        await using var fixture = new TaskServiceFixture();
        var safeMode = new SafeModeService(fixture.Paths, fixture.Terminals, NullLogger<SafeModeService>.Instance);
        using var watchingCancellation = new CancellationTokenSource();
        await using var watching = safeMode.WatchAsync(watchingCancellation.Token).GetAsyncEnumerator();
        Assert.True(await watching.MoveNextAsync());
        Assert.False(watching.Current);
        var changed = watching.MoveNextAsync().AsTask();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var callback = safeMode.RestrictedOperationsCancellation.Register(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var protection = authentication ? safeMode.EnableForAuthenticationFailure() : null;
        var enabling = Task.Run(() =>
        {
            if (authentication) protection!();
            else safeMode.SetEnabled(true);
        });
        Task<bool>? late = null;
        Task? closing = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(watching.Current);
            closing = Task.Run(() => safeMode.SetEnabled(false));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await watching.MoveNextAsync());
            Assert.False(watching.Current);
            if (reopen)
            {
                safeMode.SetEnabled(true);
                Assert.True(await watching.MoveNextAsync());
                Assert.True(watching.Current);
            }
            release.Set();
            await enabling.WaitAsync(TimeSpan.FromSeconds(5));
            late = watching.MoveNextAsync().AsTask();
            Assert.False(late.IsCompleted);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(enabling, closing ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            watchingCancellation.Cancel();
            try { await changed; } catch (OperationCanceledException) { }
            if (late is not null) try { await late; } catch (OperationCanceledException) { }
            safeMode.SetEnabled(false);
            CurrentSource(safeMode).Dispose();
        }
    }

    [Fact]
    public async Task DeferredAuthenticationCompletionOnlyCancelsItsCapturedProtectionAndTerminalSnapshot()
    {
        await using var fixture = new TaskServiceFixture();
        var safeMode = new SafeModeService(fixture.Paths, fixture.Terminals, NullLogger<SafeModeService>.Instance);
        var previous = AddTerminal(fixture.Terminals);
        var token = safeMode.RestrictedOperationsCancellation;
        var cancellations = 0;
        using var callback = token.Register(() => Interlocked.Increment(ref cancellations));
        var complete = safeMode.EnableForAuthenticationFailure();
        Assert.NotNull(complete);
        safeMode.SetEnabled(false);
        var current = AddTerminal(fixture.Terminals);
        try
        {
            Assert.False(token.IsCancellationRequested);
            Assert.NotNull(token.WaitHandle);
            complete();
            complete();
            Assert.True(token.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
            Assert.Equal(1, cancellations);
            Assert.Equal(1, previous.Kills);
            Assert.Equal(0, current.Kills);
            Assert.False(safeMode.Enabled);
            Assert.False(WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).SafeModeEnabled);
            Assert.False(safeMode.RestrictedOperationsCancellation.IsCancellationRequested);
        }
        finally { complete(); CurrentSource(safeMode).Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceFailureKeepsDistinctNormalAndAuthenticationSemantics(bool authentication)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.SafeMode.Test", Guid.NewGuid().ToString("N"));
        var paths = PortableDataPaths.Create(root);
        var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var safeMode = new SafeModeService(paths, terminals, NullLogger<SafeModeService>.Instance);
        try
        {
            using var blocked = new FileStream(paths.ConfigurationFile + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var failure = Assert.Throws<AggregateException>(() =>
            {
                if (authentication) safeMode.EnableForAuthenticationFailure()!();
                else safeMode.SetEnabled(true);
            });
            Assert.All(failure.InnerExceptions, error => Assert.IsType<IOException>(error));
            Assert.Equal(authentication, safeMode.Enabled);
            Assert.Equal(authentication, safeMode.RestrictedOperationsCancellation.IsCancellationRequested);
            Assert.False(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SafeModeEnabled);
        }
        finally
        {
            CurrentSource(safeMode).Dispose();
            await terminals.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private static CancellationTokenSource CurrentSource(SafeModeService safeMode) =>
        (CancellationTokenSource)typeof(SafeModeService).GetField("restrictedOperations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(safeMode)!;

    private static ProcessDouble AddTerminal(TerminalSessionManager manager)
    {
        var sessions = (ConcurrentDictionary<Guid, TerminalSession>)typeof(TerminalSessionManager)
            .GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        var process = new ProcessDouble();
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, _ => Task.CompletedTask,
            item => { sessions.TryRemove(item.Id, out _); return Task.CompletedTask; }, NullLogger.Instance);
        Assert.True(sessions.TryAdd(session.Id, session));
        return process;
    }

    private sealed class ProcessDouble : ITerminalProcess
    {
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; } = new MemoryStream();
        public int Kills { get; private set; }
        public int? ExitCode => Kills > 0 ? 0 : null;
        public bool HasExited() => Kills > 0;
        public void Kill() => Kills++;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Resize(short columns, short rows) { }
        public void ClosePseudoConsoleHandle() { }
        public ValueTask CloseOutputAsync() => Output.DisposeAsync();
        public ValueTask DisposeAsync() { Input.Dispose(); Output.Dispose(); return ValueTask.CompletedTask; }
    }
}
