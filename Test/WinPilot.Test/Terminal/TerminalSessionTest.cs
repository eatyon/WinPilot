using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinPilot.Features.Terminal;

namespace WinPilot.Test;

public sealed partial class TerminalSessionTest
{
    [Theory]
    [InlineData("none")]
    [InlineData("cancel")]
    [InlineData("input")]
    [InlineData("kill")]
    [InlineData("wait")]
    [InlineData("console")]
    [InlineData("output")]
    [InlineData("dispose")]
    [InlineData("source")]
    [InlineData("remove")]
    public async Task CloseIsolatesEachFailureAndCompletesRemovalAndRepeatedClose(string failedStage)
    {
        var stages = new ConcurrentQueue<string>();
        var failure = new IOException($"failure-{failedStage}");
        var process = new ProcessDouble(stages);
        var logger = new CloseLogger();
        void Stage(string stage)
        {
            stages.Enqueue(stage);
            if (stage == failedStage) throw failure;
        }
        process.OnStage = Stage;
        var source = new DisposeSource(() => Stage("source"));
        var token = source.Token;
        using var callback = token.Register(() => Stage("cancel"));
        var removed = 0;
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, _ => Task.CompletedTask, _ =>
        {
            removed++;
            Stage("remove");
            return Task.CompletedTask;
        }, logger, source);

        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync();

        Assert.Equal(1, removed);
        Assert.Equal(1, process.DisposeCount);
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => source.Token);
        Assert.Contains("input", stages);
        Assert.Contains("kill", stages);
        Assert.Contains("console", stages);
        Assert.Contains("output", stages);
        Assert.Contains("dispose", stages);
        Assert.Contains("source", stages);
        Assert.Contains("remove", stages);
        Assert.Equal(failedStage != "kill", stages.Contains("wait"));
        Assert.True(Array.IndexOf(stages.ToArray(), "console") < Array.IndexOf(stages.ToArray(), "output"));
        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        if (failedStage == "none") Assert.Empty(warnings);
        else
        {
            var warning = Assert.Single(warnings);
            Assert.Contains(session.Id.ToString(), warning.Message);
            if (failedStage == "cancel") Assert.Same(failure, Assert.Single(Assert.IsType<AggregateException>(warning.Error).InnerExceptions));
            else Assert.Same(failure, warning.Error);
        }
    }

    [Fact]
    public async Task MultipleFailuresAreLoggedByStageAndBottomAggregateIsLoggedOnce()
    {
        var process = new ProcessDouble(new());
        var inputFailure = new IOException("input");
        var consoleFailure = new IOException("console");
        var bottomFailure = new AggregateException(new IOException("bottom-input"), new IOException("bottom-output"));
        process.OnStage = stage =>
        {
            if (stage == "input") throw inputFailure;
            if (stage == "console") throw consoleFailure;
            if (stage == "dispose") throw bottomFailure;
        };
        var logger = new CloseLogger();
        var removed = false;
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, _ => Task.CompletedTask, _ =>
        {
            removed = true;
            return Task.CompletedTask;
        }, logger);

        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(removed);
        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        Assert.Equal(3, warnings.Length);
        Assert.Contains(warnings, entry => ReferenceEquals(inputFailure, entry.Error) && entry.Message.Contains("关闭输入"));
        Assert.Contains(warnings, entry => ReferenceEquals(consoleFailure, entry.Error) && entry.Message.Contains("关闭伪控制台"));
        Assert.Single(warnings.Where(entry => ReferenceEquals(bottomFailure, entry.Error)));
        Assert.Equal(1, process.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputTaskFaultIsLoggedButExpectedReadCancellationIsNot(bool expectedCancellation)
    {
        var process = new ProcessDouble(new());
        var failure = new IOException("打开输出读取失败");
        process.OutputOpenFailure = expectedCancellation ? null : failure;
        process.HonorReadCancellation = expectedCancellation;
        var logger = new CloseLogger();
        var removed = false;
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, _ => Task.CompletedTask, _ =>
        {
            removed = true;
            return Task.CompletedTask;
        }, logger);
        try
        {
            session.Start();
            if (expectedCancellation) await process.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(removed);
            Assert.Equal(1, process.DisposeCount);
            if (expectedCancellation) Assert.Empty(logger.Entries);
            else
            {
                var entry = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Contains("读取终端输出失败", entry.Message);
                Assert.Contains(session.Id.ToString(), entry.Message);
                Assert.Same(failure, entry.Error);
            }
        }
        finally { process.ReleaseOutput(); await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputIsUnblockedBeforeWaitingAndFailureRunsBottomFallback(bool outputFails)
    {
        var stages = new ConcurrentQueue<string>();
        var process = new ProcessDouble(stages);
        var failure = new IOException("output close");
        process.OnStage = stage =>
        {
            stages.Enqueue(stage);
            if (stage == "output" && outputFails) throw failure;
        };
        var logger = new CloseLogger();
        var removed = false;
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, _ => Task.CompletedTask, _ =>
        {
            stages.Enqueue("remove");
            removed = true;
            return Task.CompletedTask;
        }, logger);
        try
        {
            session.Start();
            await process.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(removed);
            Assert.Equal(1, process.DisposeCount);
            var order = stages.ToArray();
            Assert.True(Array.IndexOf(order, "console") < Array.IndexOf(order, "output"));
            Assert.True(Array.IndexOf(order, "output") < Array.IndexOf(order, "read-ended"));
            Assert.True(Array.IndexOf(order, "read-ended") < Array.IndexOf(order, "remove"));
            Assert.True(outputFails
                ? Array.IndexOf(order, "dispose") < Array.IndexOf(order, "read-ended")
                : Array.IndexOf(order, "read-ended") < Array.IndexOf(order, "dispose"));
            if (outputFails) Assert.Same(failure, Assert.Single(logger.Entries).Error);
            else Assert.Empty(logger.Entries);
        }
        finally { process.ReleaseOutput(); await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task FailedOutputCloseAndFailedFallbackDoNotPretendPendingReadHasEnded()
    {
        var process = new ProcessDouble(new());
        process.OnStage = stage =>
        {
            if (stage is "output" or "dispose") throw new IOException(stage);
        };
        var logger = new CloseLogger();
        var removed = false;
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, _ => Task.CompletedTask, _ =>
        {
            removed = true;
            return Task.CompletedTask;
        }, logger);
        Task? close = null;
        try
        {
            session.Start();
            await process.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            close = session.CloseAsync();
            await process.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(close.IsCompleted);
            Assert.False(removed);
            process.ReleaseOutput();
            await close.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(removed);
            Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        }
        finally { process.ReleaseOutput(); await (close ?? session.CloseAsync()).WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task ConcurrentCloseWaitsForOwnerAndDoesNotSkipPendingProcessWait()
    {
        var process = new ProcessDouble(new());
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Wait = () => { waiting.TrySetResult(); return release.Task; };
        var removed = 0;
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, _ => Task.CompletedTask, _ =>
        {
            removed++;
            return Task.CompletedTask;
        }, new CloseLogger());
        Task? first = null;
        Task? second = null;
        try
        {
            first = session.CloseAsync();
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = session.CloseAsync();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, process.DisposeCount);
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, removed);
            Assert.Equal(1, process.DisposeCount);
        }
        finally { release.TrySetResult(); await Task.WhenAll(first ?? session.CloseAsync(), second ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorOwnedAndExternalOwnedCloseDoNotWaitOnEachOther(bool externalOwner)
    {
        var process = new ProcessDouble(new()) { Exited = true };
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CloseLogger();
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, async item =>
        {
            if (item.Type != "exited") return;
            publishing.TrySetResult();
            await release.Task;
        }, _ => { removed.TrySetResult(); return Task.CompletedTask; }, logger);
        Task? close = null;
        try
        {
            session.Start();
            await publishing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (externalOwner)
            {
                close = session.CloseAsync();
                Assert.False(close.IsCompleted);
            }
            release.TrySetResult();
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await (close ?? session.CloseAsync()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, process.DisposeCount);
            Assert.Empty(logger.Entries);
        }
        finally { release.TrySetResult(); process.ReleaseOutput(); await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class ProcessDouble : ITerminalProcess
    {
        private readonly ConcurrentQueue<string> stages;
        private readonly OutputStream output;
        public ProcessDouble(ConcurrentQueue<string> stages)
        {
            this.stages = stages;
            Input = new InputStream(() => Stage("input"));
            output = new OutputStream(stages, ReadStarted);
        }
        public Action<string>? OnStage { get; set; }
        public Exception? OutputOpenFailure { set => output.OpenFailure = value; }
        public bool HonorReadCancellation { set => output.HonorCancellation = value; }
        public void FailRead(Exception exception) => output.Fail(exception);
        public TaskCompletionSource ReadEnded => output.Ended;
        public TaskCompletionSource SecondMonitorPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int monitorPolls;
        public bool OutputClosed { get; private set; }
        public Func<Task>? Wait { get; set; }
        public bool Exited { get; set; }
        public int DisposeCount { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream Input { get; }
        public Stream Output => output;
        public int? ExitCode => 0;
        public bool HasExited()
        {
            if (Interlocked.Increment(ref monitorPolls) >= 2) SecondMonitorPoll.TrySetResult();
            return Exited;
        }
        public void Kill() => Stage("kill");
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(CancellationToken.None, cancellationToken);
            Stage("wait");
            return Wait?.Invoke() ?? Task.CompletedTask;
        }
        public void Resize(short columns, short rows) { }
        public void ClosePseudoConsoleHandle() => Stage("console");
        public ValueTask CloseOutputAsync()
        {
            Stage("output");
            OutputClosed = true;
            ReleaseOutput();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeEntered.TrySetResult();
            Stage("dispose");
            OutputClosed = true;
            ReleaseOutput();
            return ValueTask.CompletedTask;
        }
        public void ReleaseOutput() => output.Release();
        private void Stage(string stage)
        {
            if (OnStage is not null) OnStage(stage);
            else stages.Enqueue(stage);
        }
    }

    private sealed class InputStream(Action close) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) close();
        }
    }

    private sealed class OutputStream(ConcurrentQueue<string> stages, TaskCompletionSource started) : Stream
    {
        internal Exception? OpenFailure { get; set; }
        internal bool HonorCancellation { get; set; }
        internal TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Fail(Exception exception) => released.TrySetException(exception);
        public void Release() => released.TrySetResult(0);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            try { return HonorCancellation ? await released.Task.WaitAsync(cancellationToken) : await released.Task; }
            finally
            {
                stages.Enqueue("read-ended");
                Ended.TrySetResult();
            }
        }
        public override bool CanRead => OpenFailure is not null ? throw OpenFailure : true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DisposeSource(Action dispose) : CancellationTokenSource
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) dispose();
        }
    }

    private sealed class CloseLogger : ILogger
    {
        internal ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((level, formatter(state, exception), exception));
    }
}
