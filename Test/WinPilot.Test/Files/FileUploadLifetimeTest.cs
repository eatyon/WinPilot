using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Features.Files;

namespace WinPilot.Test;

public sealed class FileUploadLifetimeTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinPilot.Upload.Test", Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private readonly FileSystemService service;

    public FileUploadLifetimeTest()
    {
        Directory.CreateDirectory(root);
        service = new(NullLogger<FileSystemService>.Instance, clock);
    }

    [Fact]
    public async Task CancelRetainsResourcesUntilActiveAndQueuedChunksExit()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 2));
        using var body = new PausingStream([1]);
        var active = service.AppendUploadChunkAsync(new(upload.UploadId, 0), body, default);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var nextBody = new MemoryStream([2]);
        var queued = service.AppendUploadChunkAsync(new(upload.UploadId, 1), nextBody, default);

        try
        {
            service.CancelUpload(upload.UploadId);
            service.CancelUpload(upload.UploadId);
            Assert.Single(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
            await Assert.ThrowsAsync<FileUploadValidationException>(() => service.AppendUploadChunkAsync(new(upload.UploadId, 0), Stream.Null, default));
        }
        finally { body.Resume.TrySetResult(); }

        await active.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<FileUploadValidationException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task CompletionWaitsForActiveChunkAndRejectsLateChunks()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 1));
        using var body = new PausingStream([42]);
        var active = service.AppendUploadChunkAsync(new(upload.UploadId, 0), body, default);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var complete = service.CompleteUploadAsync(new(upload.UploadId), default);
        try
        {
            Assert.False(complete.IsCompleted);
            await Assert.ThrowsAsync<FileUploadValidationException>(() => service.CompleteUploadAsync(new(upload.UploadId), default));
            await Assert.ThrowsAsync<FileUploadValidationException>(() => service.AppendUploadChunkAsync(new(upload.UploadId, 1), Stream.Null, default));
        }
        finally { body.Resume.TrySetResult(); }
        await active.WaitAsync(TimeSpan.FromSeconds(10));
        await complete.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(root, "file.bin")));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
    }

    [Fact]
    public async Task CancellingQueuedChunkDoesNotReleaseActiveChunksResources()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 1));
        using var body = new PausingStream([42]);
        var active = service.AppendUploadChunkAsync(new(upload.UploadId, 0), body, default);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var queued = service.AppendUploadChunkAsync(new(upload.UploadId, 1), Stream.Null, cancellation.Token);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            service.CancelUpload(upload.UploadId);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { body.Resume.TrySetResult(); }
        await active.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task DisposeRejectsNewUploadsWithoutWaitingForActiveRead()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 1));
        using var body = new PausingStream([42]);
        var active = service.AppendUploadChunkAsync(new(upload.UploadId, 0), body, default);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
            service.Dispose();
            Assert.Single(Directory.GetFiles(root));
            Assert.Throws<ObjectDisposedException>(() => service.BeginUpload(new(root, "late.bin", 1)));
            Assert.Equal(0, clock.ActiveTimers);
        }
        finally { body.Resume.TrySetResult(); }
        await active.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task DisposeDuringCompletionRetainsResourcesAndPreventsCommit()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 1));
        using var body = new PausingStream([42]);
        var active = service.AppendUploadChunkAsync(new(upload.UploadId, 0), body, default);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var complete = service.CompleteUploadAsync(new(upload.UploadId), default);
        try
        {
            await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(complete.IsCompleted);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { body.Resume.TrySetResult(); }
        await active.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => complete.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task CancelledActiveReadStillReleasesRetiredUpload()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 1));
        using var body = new PausingStream([42]);
        using var cancellation = new CancellationTokenSource();
        var active = service.AppendUploadChunkAsync(new(upload.UploadId, 0), body, cancellation.Token);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.CancelUpload(upload.UploadId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public void ConcurrentCreationAndCancellationOwnOneTimerAndCleanAllFiles()
    {
        Parallel.For(0, 64, index => service.BeginUpload(new(root, $"file-{index}.bin", 1)));
        Assert.Equal(1, clock.ActiveTimers);
        Assert.Equal(1, clock.CreatedTimers);
        var ids = Directory.GetFiles(root).Select(path => Path.GetFileName(path)[".winpilot-upload-".Length..^4]).ToArray();
        Parallel.ForEach(ids, service.CancelUpload);
        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(0, clock.ActiveTimers);

        var next = service.BeginUpload(new(root, "next.bin", 1));
        Assert.Equal(1, clock.ActiveTimers);
        service.CancelUpload(next.UploadId);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task CreationRacingDisposeCannotLeaveFilesOrTimers()
    {
        using var barrier = new Barrier(2);
        var create = Task.Run(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            try { service.BeginUpload(new(root, "file.bin", 1)); }
            catch (ObjectDisposedException) { }
        });
        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
        service.Dispose();
        await create.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task CleanupExpiresIdleUploadsButNotActiveChunksOrFreshActivity()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 2));
        clock.Advance(TimeSpan.FromMinutes(1));
        clock.Tick();
        Assert.Single(Directory.GetFiles(root));

        using var body = new PausingStream([1]);
        var active = service.AppendUploadChunkAsync(new(upload.UploadId, 0), body, default);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            clock.Tick();
            Assert.Single(Directory.GetFiles(root));
        }
        finally { body.Resume.TrySetResult(); }
        await active.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(1));
        clock.Tick();
        Assert.Single(Directory.GetFiles(root));
        clock.Advance(TimeSpan.FromMinutes(1));
        clock.Tick();
        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task PausedFileCreationDoesNotBlockOtherUploadsCancellationOrDispose()
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var creations = 0;
        using var isolated = new FileSystemService(NullLogger<FileSystemService>.Instance, clock, path =>
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (Interlocked.Increment(ref creations) == 2)
            {
                entered.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(10)))
                {
                    stream.Dispose();
                    throw new TimeoutException("File creation was not resumed");
                }
            }
            return stream;
        });
        var existing = isolated.BeginUpload(new(root, "existing.bin", 1));
        var creating = Task.Run(() => isolated.BeginUpload(new(root, "paused.bin", 1)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Task.Run(() => isolated.CancelUpload(existing.UploadId)).WaitAsync(TimeSpan.FromSeconds(10));
            var next = await Task.Run(() => isolated.BeginUpload(new(root, "next.bin", 0))).WaitAsync(TimeSpan.FromSeconds(10));
            await isolated.CompleteUploadAsync(new(next.UploadId), default);
            await Task.Run(isolated.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, clock.ActiveTimers);
            Assert.Single(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
        }
        finally { resume.Set(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => creating.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
        Assert.False(File.Exists(Path.Combine(root, "paused.bin")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(root, "next.bin")));
    }

    [Fact]
    public async Task RegistrationFailureCleansCreatedFileAndAllowsAnotherUpload()
    {
        clock.FailTimerCreation = true;
        Assert.Throws<IOException>(() => service.BeginUpload(new(root, "failed.bin", 1)));
        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(0, clock.ActiveTimers);
        clock.FailTimerCreation = false;
        var upload = service.BeginUpload(new(root, "next.bin", 0));
        await service.CompleteUploadAsync(new(upload.UploadId), default);
        Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task CompletionCancellationStopsWaitingAndDefersCleanupUntilActiveChunkExits()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 1));
        using var body = new PausingStream([42]);
        var active = service.AppendUploadChunkAsync(new(upload.UploadId, 0), body, default);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var complete = service.CompleteUploadAsync(new(upload.UploadId), cancellation.Token);
        try
        {
            Assert.False(complete.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => complete.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Single(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
            Assert.False(File.Exists(Path.Combine(root, "file.bin")));
        }
        finally { body.Resume.TrySetResult(); }
        await active.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task AlreadyCancelledCompletionDoesNotCloseUpload()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 1));
        using var content = new MemoryStream([42]);
        await service.AppendUploadChunkAsync(new(upload.UploadId, 0), content, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CompleteUploadAsync(new(upload.UploadId), cancellation.Token));
        await service.CompleteUploadAsync(new(upload.UploadId), default);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(root, "file.bin")));
    }

    [Fact]
    public async Task CancelWinsBeforeCommitAndWaitsForActiveChunkCleanup()
    {
        var upload = service.BeginUpload(new(root, "file.bin", 1));
        using var body = new PausingStream([42]);
        var active = service.AppendUploadChunkAsync(new(upload.UploadId, 0), body, default);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var complete = service.CompleteUploadAsync(new(upload.UploadId), default);
        try
        {
            service.CancelUpload(upload.UploadId);
            await Assert.ThrowsAsync<FileUploadValidationException>(() => complete.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Single(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
            Assert.False(File.Exists(Path.Combine(root, "file.bin")));
        }
        finally { body.Resume.TrySetResult(); }
        await active.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitRejectsCancellationAndReleasesSessionAfterCompletion(bool failCommit)
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var commits = 0;
        using var isolated = new FileSystemService(NullLogger<FileSystemService>.Instance, clock,
            commitUploadFile: (source, target) =>
            {
                Interlocked.Increment(ref commits);
                entered.Set();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
                if (failCommit) throw new IOException("commit failed");
                File.Move(source, target);
            });
        var upload = isolated.BeginUpload(new(root, "file.bin", 0));
        var complete = Task.Run(() => isolated.CompleteUploadAsync(new(upload.UploadId), default));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<FileUploadConflictException>(() => isolated.CancelUpload(upload.UploadId));
            await Assert.ThrowsAsync<FileUploadValidationException>(() => isolated.CompleteUploadAsync(new(upload.UploadId), default));
        }
        finally { resume.Set(); }
        if (failCommit)
        {
            Assert.Equal("commit failed", (await Assert.ThrowsAsync<IOException>(() => complete.WaitAsync(TimeSpan.FromSeconds(10)))).Message);
            Assert.Empty(Directory.GetFiles(root));
        }
        else
        {
            await complete.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(Path.Combine(root, "file.bin")));
        }
        await Assert.ThrowsAsync<FileUploadValidationException>(() => isolated.CompleteUploadAsync(new(upload.UploadId), default));
        isolated.CancelUpload(upload.UploadId);
        Assert.Equal(1, commits);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
    }

    public void Dispose()
    {
        service.Dispose();
        Directory.Delete(root, true);
    }

    private sealed class PausingStream(byte[] content) : MemoryStream(content)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool paused;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!paused)
            {
                paused = true;
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public int CreatedTimers => timers.Count;
        public int ActiveTimers => timers.Count(timer => !timer.Disposed);
        public bool FailTimerCreation { get; set; }
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
        public void Tick()
        {
            foreach (var timer in timers.ToArray()) timer.Tick();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (FailTimerCreation) throw new IOException("Timer creation failed");
            var timer = new ManualTimer(callback, state);
            timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }
        public void Tick() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
