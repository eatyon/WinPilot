using System.Collections.Concurrent;
using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Tasks;
using WinPilot.Features.Security;
using WinPilot.Features.Tasks;
using WinPilot.Features.Terminal;
using WinPilot.PluginContract;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class CancellationCallbackCleanupTest
{

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SessionRevocationCompletesAndReleasesSourcesWhenCallbackThrows(bool dispose, bool throwCallback)
    {
        var logger = new CallbackLogger<SessionManager>();
        using var services = new ServiceCollection().AddSingleton<ILogger<SessionManager>>(logger).BuildServiceProvider();
        var sessions = ActivatorUtilities.CreateInstance<SessionManager>(services);
        var failure = new InvalidOperationException("测试会话取消回调失败");
        var tokens = new List<CancellationToken>();
        var ids = new List<string>();
        var registrations = new List<CancellationTokenRegistration>();
        var callbacks = 0;
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var id = sessions.Create();
                ids.Add(id);
                Assert.True(sessions.TryValidate(id, out var token));
                tokens.Add(token);
                registrations.Add(token.Register(() =>
                {
                    if (Interlocked.Increment(ref callbacks) == 1 && throwCallback) throw failure;
                }));
            }

            if (dispose) sessions.Dispose();
            else sessions.RevokeAll();

            Assert.Equal(3, callbacks);
            Assert.All(tokens, token =>
            {
                Assert.True(token.IsCancellationRequested);
                Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
            });
            Assert.All(ids, id => Assert.False(sessions.Validate(id)));
            AssertWarning(logger.Entries, failure, throwCallback);

            if (dispose)
            {
                sessions.Dispose();
                Assert.Throws<ObjectDisposedException>(() => sessions.Create());
            }
            else
            {
                sessions.RevokeAll();
                Assert.True(sessions.Validate(sessions.Create()));
            }
            Assert.Equal(3, callbacks);
            AssertWarning(logger.Entries, failure, throwCallback);
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
            sessions.RevokeAll();
            sessions.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafeModePublishesStateAndClosesTerminalsWhenCallbackThrows(bool throwCallback)
    {
        await using var fixture = new TaskServiceFixture();
        var logger = new CallbackLogger<SafeModeService>();
        var safeMode = new SafeModeService(fixture.Paths, fixture.Terminals, logger);
        var sessions = (ConcurrentDictionary<Guid, TerminalSession>)typeof(TerminalSessionManager)
            .GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Terminals)!;
        var process = new TerminalProcess();
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, _ => Task.CompletedTask, item =>
        {
            sessions.TryRemove(item.Id, out _);
            return Task.CompletedTask;
        }, NullLogger.Instance);
        Assert.True(sessions.TryAdd(session.Id, session));
        using var watching = new CancellationTokenSource();
        await using var events = safeMode.WatchAsync(watching.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.False(events.Current);
        var changed = events.MoveNextAsync().AsTask();
        var failure = new InvalidOperationException("测试安全模式取消回调失败");
        var restricted = safeMode.RestrictedOperationsCancellation;
        using var callback = restricted.Register(() => { if (throwCallback) throw failure; });
        try
        {
            safeMode.SetEnabled(true);

            Assert.True(safeMode.Enabled);
            Assert.True(WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).SafeModeEnabled);
            Assert.True(restricted.IsCancellationRequested);
            Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(events.Current);
            Assert.Empty(sessions);
            Assert.Equal(1, process.KillCount);
            Assert.Equal(1, process.DisposeCount);
            AssertWarning(logger.Entries, failure, throwCallback);
            safeMode.SetEnabled(true);
            Assert.Equal(1, process.DisposeCount);
            AssertWarning(logger.Entries, failure, throwCallback);
        }
        finally
        {
            watching.Cancel();
            try { await changed; } catch (OperationCanceledException) when (watching.IsCancellationRequested) { }
            ((CancellationTokenSource)typeof(SafeModeService).GetField("restrictedOperations", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(safeMode)!).Dispose();
        }
    }

    private static void AssertWarning(IEnumerable<(LogLevel Level, string Message, Exception? Error)> entries, Exception failure, bool expected)
    {
        var warnings = entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        if (!expected) { Assert.Empty(warnings); return; }
        var warning = Assert.Single(warnings);
        Assert.Same(failure, Assert.Single(Assert.IsType<AggregateException>(warning.Error).InnerExceptions));
    }

    private sealed class CallbackLogger<T> : ILogger<T>
    {
        internal ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();
        internal Action<string>? OnMessage { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Enqueue((level, message, exception));
            OnMessage?.Invoke(message);
        }
    }

    private sealed class TerminalProcess : ITerminalProcess
    {
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; } = new MemoryStream();
        public int KillCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int? ExitCode => KillCount == 0 ? null : 0;
        public bool HasExited() => KillCount != 0;
        public void Kill() => KillCount++;
        public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
        public void Resize(short columns, short rows) { }
        public void ClosePseudoConsoleHandle() { }
        public ValueTask CloseOutputAsync() => Output.DisposeAsync();
        public async ValueTask DisposeAsync() { DisposeCount++; await Input.DisposeAsync(); await Output.DisposeAsync(); }
    }
}
