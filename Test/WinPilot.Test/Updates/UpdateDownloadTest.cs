using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Features.Updates;

namespace WinPilot.Test;

public sealed class UpdateDownloadTest : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "WinPilot.Download.Test", Guid.NewGuid().ToString("N"));
    private string Destination => Path.Combine(directory, "package.zip");

    public UpdateDownloadTest() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledDownloadTimesOutAndCallerCancellationRemainsCancellation(bool cancelCaller)
    {
        var source = new DownloadStream(TimeSpan.Zero, 1, stall: true);
        using var service = Create(source);
        using var cancellation = new CancellationTokenSource();
        var download = service.DownloadFileAsync(new("https://example.test/package.zip"), Destination,
            (_, _) => { }, cancellation.Token, TimeSpan.FromMilliseconds(200));
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        }
        else Assert.Equal("下载更新包超时，请重试", (await Assert.ThrowsAsync<TimeoutException>(() => download)).Message);
        Assert.True(source.Disposed);
        using var file = File.Open(Destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ContinuousProgressResetsIdleTimeoutAndCompletesLongerDownload()
    {
        const int chunks = 20;
        var source = new DownloadStream(TimeSpan.FromMilliseconds(200), chunks);
        using var service = Create(source);
        var progress = new List<long>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.DownloadFileAsync(new("https://example.test/package.zip"), Destination,
            (bytes, _) => progress.Add(bytes), cancellation.Token, TimeSpan.FromSeconds(3));
        Assert.Equal(Enumerable.Range(1, chunks).Select(index => (long)index), progress);
        Assert.Equal(Enumerable.Repeat((byte)1, chunks), await File.ReadAllBytesAsync(Destination));
        Assert.True(source.Disposed);
    }

    private static UpdateCheckService Create(Stream source)
        => new(NullLogger<UpdateCheckService>.Instance, new HttpClient(new Handler(source)), currentVersion: "1.0.0");

    private sealed class Handler(Stream source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
    }

    private sealed class DownloadStream(TimeSpan delay, int chunks, bool stall = false) : MemoryStream(new byte[chunks])
    {
        private int remaining = chunks;
        public bool Disposed { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (remaining == 0) return 0;
            ReadStarted.TrySetResult();
            await Task.Delay(stall ? Timeout.InfiniteTimeSpan : delay, cancellationToken);
            buffer.Span[0] = 1;
            remaining--;
            return 1;
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    public void Dispose() => Directory.Delete(directory, true);
}
