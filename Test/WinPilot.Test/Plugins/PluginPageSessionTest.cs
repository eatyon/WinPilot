using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Features.Plugins;

namespace WinPilot.Test;

public sealed class PluginPageSessionTest
{
    [Fact]
    public async Task ClosingDefersResourceDisposalUntilActiveHandlerExits()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        CancellationToken token = default;
        var id = sessions.Open("plugin", "page", session =>
        {
            session.RegisterResource(new Resource(() => disposed++));
            session.RegisterMessageHandler(async (_, cancellation) =>
            {
                token = cancellation;
                await release.Task;
                Assert.Equal(0, disposed);
                return "{}";
            });
        });
        var request = sessions.SendAsync(id, "{}", CancellationToken.None);
        try
        {
            sessions.Close(id);
            Assert.True(token.IsCancellationRequested);
            Assert.Equal(0, disposed);
        }
        finally { release.TrySetResult(); await request; }
        Assert.Equal(1, disposed);
        sessions.Close(id);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task ClosingSessionCancelsUploadBodyReadingBeforePluginHandler()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var handled = false;
        var id = sessions.Open("plugin", "page", session => session.RegisterUploadHandler((_, _) =>
        {
            handled = true;
            return Task.FromResult("{}");
        }));
        var request = sessions.UploadAsync(id, async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new WinPilot.PluginContract.PluginPageUpload("chunk", "{}", new byte[] { 1 });
        }, CancellationToken.None);
        sessions.Close(id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.False(handled);
    }

    [Fact]
    public async Task PageWithoutUploadHandlerRejectsBeforeReadingBody()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var id = sessions.Open("plugin", "page", _ => { });
        await Assert.ThrowsAsync<PluginOperationException>(() => sessions.UploadAsync(id, _ =>
        {
            Assert.Fail("不支持上传的页面不应读取上传数据");
            return Task.FromResult(new WinPilot.PluginContract.PluginPageUpload("chunk", "{}", new byte[] { 1 }));
        }, CancellationToken.None));
    }
    [Fact]
    public async Task UploadsAreCancelledWhenSessionCloses()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var id = sessions.Open("plugin", "page", session => session.RegisterUploadHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }));
        var request = sessions.UploadAsync(id, _ => Task.FromResult(new WinPilot.PluginContract.PluginPageUpload("chunk", "{}", new byte[] { 1 })), CancellationToken.None);
        sessions.Close(id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sessions.UploadAsync(id, _ => Task.FromResult(new WinPilot.PluginContract.PluginPageUpload("chunk", "{}", new byte[] { 1 })), CancellationToken.None));
    }
    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotPreventResourceCleanup()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var disposed = 0;
        var id = sessions.Open("plugin", "page", session =>
        {
            session.RegisterResource(new Resource(() => disposed++));
            session.RegisterMessageHandler(async (_, token) =>
            {
                using var registration = token.Register(() => throw new InvalidOperationException("cancel failed"));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unreachable";
            });
        });
        var request = sessions.SendAsync(id, "request", CancellationToken.None);

        sessions.Close(id);
        sessions.Close(id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task CancellationCallbackDoesNotBlockOtherPageSessions()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = sessions.Open("plugin", "first", session =>
            session.RegisterMessageHandler(async (_, token) =>
            {
                using var registration = token.Register(() => { entered.TrySetResult(); release.Wait(); });
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unreachable";
            }));
        var other = sessions.Open("plugin", "second", _ => { });
        var request = sessions.SendAsync(id, "request", CancellationToken.None);
        var closing = Task.Run(() => sessions.Close(id));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => sessions.Touch(other)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        await closing;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchCloseRemovesSelectedSessionsBeforeRunningPluginCleanup(bool closeAll)
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var other = "";
        var removedBeforeCleanup = false;
        var disposed = 0;
        sessions.Open("plugin", "first", session => session.RegisterResource(new Resource(() =>
        {
            try { sessions.Touch(other); }
            catch (KeyNotFoundException) { removedBeforeCleanup = true; }
            disposed++;
        })));
        other = sessions.Open("plugin", "second", session => session.RegisterResource(new Resource(() => disposed++)));
        var independent = sessions.Open("other-plugin", "page", _ => { });

        if (closeAll) sessions.CloseAll();
        else sessions.CloseByPlugin("plugin");

        Assert.True(removedBeforeCleanup);
        Assert.Equal(2, disposed);
        if (closeAll) Assert.Throws<KeyNotFoundException>(() => sessions.Touch(independent));
        else sessions.Touch(independent);
    }

    private sealed class Resource(Action cleanup) : IDisposable
    {
        public void Dispose() => cleanup();
    }

    [Fact]
    public async Task PageSession_ForwardsMessagesAndRejectsAfterClose()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var id = sessions.Open("test-plugin", "test-page", session => session.RegisterMessageHandler((message, _) => Task.FromResult("reply:" + message)));

        Assert.Equal("reply:request", await sessions.SendAsync(id, "request", CancellationToken.None));

        sessions.Close(id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sessions.SendAsync(id, "request", CancellationToken.None));
    }

    [Fact]
    public async Task PageSession_CloseAllCancelsActiveRequestsAndRemovesSessions()
    {
        using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var id = sessions.Open("test-plugin", "test-page", session =>
            session.RegisterMessageHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "unreachable";
            }));
        var request = sessions.SendAsync(id, "request", CancellationToken.None);

        sessions.CloseAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sessions.SendAsync(id, "request", CancellationToken.None));
    }
}
