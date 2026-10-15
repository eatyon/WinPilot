using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class ConPtyTerminalProcessTest
{
    [Fact]
    public void ResizeAndClosePreserveHandleDimensionsAndSingleRelease()
    {
        var calls = new List<string>();
        var console = new ConPtyTerminalProcess.PseudoConsoleHandle((IntPtr)123,
            (handle, columns, rows) => calls.Add($"resize:{handle}:{columns}:{rows}"),
            handle => calls.Add($"close:{handle}"));

        console.Resize(100, 30);
        console.Close();
        console.Close();

        Assert.Equal(new[] { "resize:123:100:30", "close:123" }, calls);
        Assert.Throws<ObjectDisposedException>(() => console.Resize(80, 25));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseWaitsForResizeAndOnlyClosingCallerReleasesHandle(bool failResize)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var failure = new IOException("resize-failure");
        var resizeCalls = 0;
        var closeCalls = 0;
        var closeThread = 0;
        var console = new ConPtyTerminalProcess.PseudoConsoleHandle((IntPtr)123, (_, _, _) =>
        {
            Interlocked.Increment(ref resizeCalls);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            if (failResize) throw failure;
        }, handle =>
        {
            Assert.Equal((IntPtr)123, handle);
            closeThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref closeCalls);
        });
        Exception? resizeFailure = null;
        var resize = StartThread(() => resizeFailure = Record.Exception(() => console.Resize(100, 30)));
        (Thread Thread, Task Completion)? closing = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            closing = StartThread(console.Close);
            Assert.True(SpinWait.SpinUntil(() => closing.Value.Thread.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(5)));
            Assert.False(closing.Value.Completion.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref closeCalls));
            Assert.Throws<ObjectDisposedException>(() => console.Resize(80, 25));
            Assert.Equal(1, Volatile.Read(ref resizeCalls));
            release.Set();
            await Task.WhenAll(resize.Completion, closing.Value.Completion).WaitAsync(TimeSpan.FromSeconds(5));
            if (failResize) Assert.Same(failure, resizeFailure);
            else Assert.Null(resizeFailure);
            Assert.Equal(1, closeCalls);
            Assert.Equal(closing.Value.Thread.ManagedThreadId, closeThread);
            Assert.NotEqual(resize.Thread.ManagedThreadId, closeThread);
            console.Close();
            Assert.Equal(1, closeCalls);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(resize.Completion, closing?.Completion ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            console.Close();
        }
    }

    [Fact]
    public async Task CloseWaitsForEveryConcurrentResize()
    {
        using var entered = new CountdownEvent(2);
        using var firstRelease = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        var closed = 0;
        var console = new ConPtyTerminalProcess.PseudoConsoleHandle((IntPtr)123, (_, columns, _) =>
        {
            entered.Signal();
            Assert.True((columns == 100 ? firstRelease : secondRelease).Wait(TimeSpan.FromSeconds(10)));
        }, _ => Interlocked.Increment(ref closed));
        var first = StartThread(() => console.Resize(100, 30));
        var second = StartThread(() => console.Resize(101, 30));
        (Thread Thread, Task Completion)? closing = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            closing = StartThread(console.Close);
            Assert.True(SpinWait.SpinUntil(() => closing.Value.Thread.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(5)));
            firstRelease.Set();
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(closing.Value.Completion.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref closed));
            secondRelease.Set();
            await Task.WhenAll(second.Completion, closing.Value.Completion).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, closed);
        }
        finally
        {
            firstRelease.Set();
            secondRelease.Set();
            await Task.WhenAll(first.Completion, second.Completion, closing?.Completion ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            console.Close();
        }
    }

    [Fact]
    public async Task ConcurrentCloseDoesNotRepeatNativeReleaseOrHoldStateLock()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var closed = 0;
        var console = new ConPtyTerminalProcess.PseudoConsoleHandle((IntPtr)123,
            (_, _, _) => throw new InvalidOperationException("不应进入原生 Resize"), _ =>
            {
                Interlocked.Increment(ref closed);
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            });
        var closing = StartThread(console.Close);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var other = StartThread(() =>
            {
                Assert.Throws<ObjectDisposedException>(() => console.Resize(100, 30));
                console.Close();
            });
            await other.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(closing.Completion.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref closed));
        }
        finally
        {
            release.Set();
            await closing.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, closed);
    }

    [Fact]
    public void FailedCloseIsNotRetriedAndPreservesOriginalException()
    {
        var failure = new IOException("close-failure");
        var closed = 0;
        var console = new ConPtyTerminalProcess.PseudoConsoleHandle((IntPtr)123, (_, _, _) => { }, _ =>
        {
            closed++;
            throw failure;
        });

        Assert.Same(failure, Assert.Throws<IOException>(console.Close));
        console.Close();
        Assert.Equal(1, closed);
        Assert.Throws<ObjectDisposedException>(() => console.Resize(100, 30));
    }

    [Fact]
    public void EmptyHandleDoesNotInvokeNativeOperations()
    {
        var console = new ConPtyTerminalProcess.PseudoConsoleHandle(IntPtr.Zero,
            (_, _, _) => throw new InvalidOperationException("不应进入原生 Resize"),
            _ => throw new InvalidOperationException("不应进入原生关闭"));

        Assert.Throws<ObjectDisposedException>(() => console.Resize(100, 30));
        console.Close();
        console.Close();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(15)]
    public async Task ReleaseAttemptsEveryResourceInOrderAndPreservesOriginalFailures(int failureMask)
    {
        var stages = new List<int>();
        var failures = Enumerable.Range(0, 4).Select(index => new IOException($"release-{index}")).ToArray();
        void Release(int index)
        {
            stages.Add(index);
            if ((failureMask & (1 << index)) != 0) throw failures[index];
        }
        var input = new ReleaseStream(() => Release(0));
        var output = new ReleaseStream(() => Release(1));
        var handle = new ReleaseHandle(() => Release(2));

        var error = await Record.ExceptionAsync(() => ConPtyTerminalProcess.ReleaseResourcesAsync(input, output, handle, () => Release(3)).AsTask());

        Assert.Equal(new[] { 0, 1, 2, 3 }, stages);
        var expected = failures.Where((_, index) => (failureMask & (1 << index)) != 0).ToArray();
        if (expected.Length == 0) Assert.Null(error);
        else if (expected.Length == 1) Assert.Same(expected[0], error);
        else
        {
            var aggregate = Assert.IsType<AggregateException>(error);
            Assert.Equal(expected.Length, aggregate.InnerExceptions.Count);
            for (var index = 0; index < expected.Length; index++) Assert.Same(expected[index], aggregate.InnerExceptions[index]);
        }
    }

    private static (Thread Thread, Task Completion) StartThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.Start();
        return (thread, completion.Task);
    }

    private sealed class ReleaseStream(Action release) : MemoryStream
    {
        public override ValueTask DisposeAsync()
        {
            base.Dispose();
            release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReleaseHandle(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
