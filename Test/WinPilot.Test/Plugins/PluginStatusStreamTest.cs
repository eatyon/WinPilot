using System.Text;
using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Plugins;
using WinPilot.Features.Plugins;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class PluginStatusStreamTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winpilot-status-stream", Guid.NewGuid().ToString("N"));
    private readonly PluginPageSessionManager pages = new(NullLogger<PluginPageSessionManager>.Instance);
    private readonly Runtime runtime = new();
    private readonly PluginManager manager;

    public PluginStatusStreamTest()
    {
        var paths = PortableDataPaths.Create(root);
        var directory = Path.Combine(paths.PluginsDirectory, "test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            """{"id":"test","name":"Test","version":"1.0.0","main":"plugin.dll"}""");
        File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
        manager = new(paths, NullLogger<PluginManager>.Instance, runtime, pages);
        manager.Enable("test");
    }

    [Fact]
    public async Task FirstWriteFailureAlwaysRemovesSubscription()
    {
        using var stream = new ResponseStream((_, _) => Task.FromException(new IOException("连接已断开")));
        var context = new DefaultHttpContext();
        context.Response.Body = stream;
        await Assert.ThrowsAsync<IOException>(() => PluginEndpointRouteBuilderExtensions.StreamPluginStatusAsync("test", context, manager));
        Assert.Equal(0, runtime.Instance.Subscribers);
    }

    [Fact]
    public async Task InitialStatusFailureAlwaysRemovesSubscription()
    {
        runtime.Instance.FailStatusRead = true;
        await Assert.ThrowsAsync<PluginOperationException>(() =>
            PluginEndpointRouteBuilderExtensions.StreamPluginStatusAsync("test", new DefaultHttpContext(), manager));
        Assert.Equal(0, runtime.Instance.Subscribers);
    }

    [Fact]
    public async Task SlowConnectionKeepsOnlyLatestPendingStatusAndDisconnectRemovesSubscription()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new ResponseStream(async (count, token) =>
        {
            if (count != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }, count => { if (count == 2) cancellation.Cancel(); });
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Response.Body = stream;
        var streaming = PluginEndpointRouteBuilderExtensions.StreamPluginStatusAsync("test", context, manager);
        try
        {
            await entered.Task.WaitAsync(cancellation.Token);
            Assert.Equal(1, runtime.Instance.Subscribers);
            for (var sequence = 1; sequence <= 1000; sequence++) runtime.Instance.Publish(sequence);
            release.TrySetResult();
            await streaming.WaitAsync(TimeSpan.FromSeconds(5));
            var messages = Encoding.UTF8.GetString(stream.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(new[] { "data: {\"sequence\":0}", "data: {\"sequence\":1000}" }, messages);
            Assert.Equal(0, runtime.Instance.Subscribers);
        }
        finally
        {
            release.TrySetResult();
            cancellation.Cancel();
            await streaming;
        }
    }

    [Fact]
    public async Task ApplicationStoppingEndsStreamAndRemovesSubscription()
    {
        using var lifetime = new Lifetime();
        using var stream = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = stream;
        var middleware = new RequestCancellationMiddleware(async request =>
            await PluginEndpointRouteBuilderExtensions.StreamPluginStatusAsync("test", request, manager));
        var streaming = middleware.InvokeAsync(context, lifetime);
        Assert.Equal(1, runtime.Instance.Subscribers);
        lifetime.StopApplication();
        await streaming.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, runtime.Instance.Subscribers);
        Assert.False(context.RequestAborted.IsCancellationRequested);
    }

    [Fact]
    public async Task InitialAndSubsequentStatusEventsFlushBeforeWaitingForNextEvent()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initialFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new FlushStream((count, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            if (count == 1) initialFlushed.TrySetResult();
            else nextFlushed.TrySetResult();
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        using var body = new FlushBodyFeature(stream);
        context.Features.Set<IHttpResponseBodyFeature>(body);
        var streaming = PluginEndpointRouteBuilderExtensions.StreamPluginStatusAsync("test", context, manager);
        try
        {
            await initialFlushed.Task.WaitAsync(cancellation.Token);
            Assert.Equal(1, runtime.Instance.Subscribers);
            Assert.Equal("text/event-stream", context.Response.ContentType);
            Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
            runtime.Instance.Publish(1);
            await nextFlushed.Task.WaitAsync(cancellation.Token);
            Assert.Equal(2, stream.FlushCount);
            Assert.Equal(new[] { "data: {\"sequence\":0}", "data: {\"sequence\":1}" },
                Encoding.UTF8.GetString(body.ReadWrittenBytes()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries));
        }
        finally { cancellation.Cancel(); await streaming.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(0, runtime.Instance.Subscribers);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FlushFailureAlwaysRemovesSubscription(int failingFlush)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = new FlushStream((count, _) =>
        {
            if (count == failingFlush) throw new IOException("连接已断开");
            runtime.Instance.Publish(1);
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        using var body = new FlushBodyFeature(stream);
        context.Features.Set<IHttpResponseBodyFeature>(body);
        await Assert.ThrowsAsync<IOException>(() => PluginEndpointRouteBuilderExtensions.StreamPluginStatusAsync("test", context, manager)
            .WaitAsync(cancellation.Token));
        Assert.Equal(failingFlush, stream.FlushCount);
        Assert.Equal(0, runtime.Instance.Subscribers);
    }

    private sealed class FlushStream(Func<int, CancellationToken, Task> flush) : MemoryStream
    {
        public int FlushCount { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken) => flush(++FlushCount, cancellationToken);
    }

    private sealed class FlushBodyFeature(Stream stream) : IHttpResponseBodyFeature, IDisposable
    {
        private readonly Pipe pipe = new();
        public Stream Stream => stream;
        public PipeWriter Writer => pipe.Writer;
        public void DisableBuffering() { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync() => Task.CompletedTask;
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public byte[] ReadWrittenBytes()
        {
            Assert.True(pipe.Reader.TryRead(out var result));
            var bytes = result.Buffer.ToArray();
            pipe.Reader.AdvanceTo(result.Buffer.End);
            return bytes;
        }
        public void Dispose() { pipe.Writer.Complete(); pipe.Reader.Complete(); }
    }

    public void Dispose()
    {
        manager.Dispose();
        pages.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }

    private sealed class ResponseStream(Func<int, CancellationToken, Task> before, Action<int>? after = null) : MemoryStream
    {
        private int count;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var current = ++count;
            await before(current, cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
            after?.Invoke(current);
        }
    }

    private sealed class Runtime : IPluginRuntime
    {
        public RuntimeInstance Instance { get; } = new();
        public IPluginRuntimeInstance Start(PluginInfo plugin) => Instance;
    }

    private sealed class RuntimeInstance : IPluginRuntimeInstance, IPluginRuntimeStatus, IPluginRuntimeStatusSubscriptions
    {
        public int Subscribers { get; private set; }
        public bool FailStatusRead { get; set; }
        public string? StatusJson => FailStatusRead ? throw new InvalidOperationException("状态读取失败") : "{\"sequence\":0}";
        public event Action<string>? StateChanged;
        public void Publish(int sequence) => StateChanged?.Invoke($"{{\"sequence\":{sequence}}}");
        public void SetStatusSubscriberCount(int count) => Subscribers = count;
        public void Dispose() => StateChanged = null;
    }
}
