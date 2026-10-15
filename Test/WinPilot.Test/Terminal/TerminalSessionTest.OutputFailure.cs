using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinPilot.Features.Terminal;

namespace WinPilot.Test;

public sealed partial class TerminalSessionTest
{
    [Theory]
    [InlineData("io", false)]
    [InlineData("disposed", false)]
    [InlineData("operation", false)]
    [InlineData("cancel", false)]
    [InlineData("io", true)]
    public async Task OutputReadFailureNotifiesAndEndsOnlyItsSessionWithoutWaitingOnItself(string kind, bool notificationFails)
    {
        var process = new ProcessDouble(new());
        var healthyProcess = new ProcessDouble(new());
        var source = new CancellationTokenSource();
        var token = source.Token;
        var healthySource = new CancellationTokenSource();
        var healthyToken = healthySource.Token;
        var logger = new CloseLogger();
        var events = new ConcurrentQueue<TerminalEvent>();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = ReadFailure(kind);
        var notificationFailure = new IOException("通知失败");
        var removeCount = 0;
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, async item =>
        {
            events.Enqueue(item);
            if (item.Type == "error")
            {
                errorEntered.TrySetResult();
                await releaseError.Task;
                if (notificationFails) throw notificationFailure;
            }
        }, _ => { removeCount++; removed.TrySetResult(); return Task.CompletedTask; }, logger, source);
        var healthy = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), healthyProcess,
            _ => Task.CompletedTask, _ => Task.CompletedTask, new CloseLogger(), healthySource);
        try
        {
            session.Start();
            healthy.Start();
            await process.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            process.FailRead(failure);
            await errorEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var close = session.CloseAsync();
            Assert.False(close.IsCompleted);
            releaseError.TrySetResult();
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await close.WaitAsync(TimeSpan.FromSeconds(3));
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Collection(events,
                item => { Assert.Equal("error", item.Type); Assert.Equal(session.Id, item.SessionId); Assert.Equal("终端输出读取失败，正在关闭会话", item.Data); },
                item => { Assert.Equal("exited", item.Type); Assert.Equal(session.Id, item.SessionId); Assert.Equal("终端会话已结束", item.Data); });
            Assert.Equal(1, removeCount);
            Assert.Equal(1, process.DisposeCount);
            Assert.True(token.IsCancellationRequested);
            var warning = Assert.Single(logger.Entries.Where(entry => ReferenceEquals(entry.Error, failure)));
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Contains(session.Id.ToString(), warning.Message);
            if (notificationFails) Assert.Single(logger.Entries.Where(entry => ReferenceEquals(entry.Error, notificationFailure)));
            Assert.False(healthyToken.IsCancellationRequested);
            Assert.Equal(0, healthyProcess.DisposeCount);
            await healthy.WriteAsync("still running");
            Assert.True(healthyProcess.Input.Length > 0);
        }
        finally
        {
            releaseError.TrySetResult();
            process.ReleaseOutput();
            healthyProcess.ReleaseOutput();
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await healthy.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task FailedReadClosesOutputBeforeConptyAndDoesNotPretendBlockedConsoleHasCompleted()
    {
        var process = new ProcessDouble(new());
        var logger = new CloseLogger();
        using var releaseConsole = new ManualResetEventSlim();
        var consoleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<TerminalEvent>();
        var outputClosedBeforeConsole = false;
        process.OnStage = stage =>
        {
            if (stage != "console") return;
            outputClosedBeforeConsole = process.OutputClosed;
            consoleEntered.TrySetResult();
            if (!releaseConsole.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("测试未释放伪控制台关闭");
        };
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process,
            item => { events.Enqueue(item); return Task.CompletedTask; },
            _ => { removed.TrySetResult(); return Task.CompletedTask; }, logger);
        try
        {
            session.Start();
            await process.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            process.FailRead(new IOException("输出失败"));
            await consoleEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(outputClosedBeforeConsole);
            Assert.False(removed.Task.IsCompleted);
            Assert.DoesNotContain(events, item => item.Type == "exited");
            var close = session.CloseAsync();
            Assert.False(close.IsCompleted);
            releaseConsole.Set();
            await close.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(removed.Task.IsCompletedSuccessfully);
            Assert.Single(events.Where(item => item.Type == "exited"));
            Assert.Equal(1, process.DisposeCount);
        }
        finally { releaseConsole.Set(); process.ReleaseOutput(); await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task EndNotificationFailureDoesNotPreventRemovalOrRepeatedCloseCompletion()
    {
        var process = new ProcessDouble(new());
        var logger = new CloseLogger();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationFailure = new IOException("结束通知失败");
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process,
            item => item.Type == "exited" ? Task.FromException(notificationFailure) : Task.CompletedTask,
            _ => { removed.TrySetResult(); return Task.CompletedTask; }, logger);
        try
        {
            session.Start();
            await process.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            process.FailRead(new IOException("读取失败"));
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Single(logger.Entries.Where(entry => ReferenceEquals(entry.Error, notificationFailure)));
            Assert.Equal(1, process.DisposeCount);
        }
        finally { process.ReleaseOutput(); await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task NormalEofDoesNotNotifyOrCloseARunningProcess()
    {
        var process = new ProcessDouble(new());
        var logger = new CloseLogger();
        var events = new ConcurrentQueue<TerminalEvent>();
        var removed = 0;
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process,
            item => { events.Enqueue(item); return Task.CompletedTask; },
            _ => { removed++; return Task.CompletedTask; }, logger);
        try
        {
            session.Start();
            await process.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            process.ReleaseOutput();
            await process.ReadEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await process.SecondMonitorPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, removed);
            Assert.Equal(0, process.DisposeCount);
            Assert.Empty(events);
            Assert.Empty(logger.Entries);
        }
        finally { process.ReleaseOutput(); await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    public async Task ReadFailureDuringNormalCloseDoesNotReportFatalOutputFailure(string kind)
    {
        var process = new ProcessDouble(new());
        process.OnStage = stage => { if (stage == "output") process.FailRead(ReadFailure(kind)); };
        var logger = new CloseLogger();
        var events = new ConcurrentQueue<TerminalEvent>();
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process,
            item => { events.Enqueue(item); return Task.CompletedTask; }, _ => Task.CompletedTask, logger);
        try
        {
            session.Start();
            await process.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(events);
            Assert.Empty(logger.Entries);
            Assert.Equal(1, process.DisposeCount);
        }
        finally { process.ReleaseOutput(); await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static Exception ReadFailure(string kind) => kind switch
    {
        "disposed" => new ObjectDisposedException("private-output"),
        "operation" => new InvalidOperationException("private-output"),
        "cancel" => new OperationCanceledException("unexpected cancellation"),
        _ => new IOException("private-output")
    };
}
