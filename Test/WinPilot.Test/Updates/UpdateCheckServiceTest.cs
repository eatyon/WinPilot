using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Features.Updates;

namespace WinPilot.Test;

public sealed class UpdateCheckServiceTest
{
    [Theory]
    [InlineData(-1, true, true, false, false)]
    [InlineData(0, true, true, false, false)]
    [InlineData(1, true, true, true, true)]
    [InlineData(1, false, true, true, false)]
    [InlineData(1, true, false, true, false)]
    public async Task ReleaseVersionAndPackageDigestDetermineAvailability(int offset, bool package, bool digest, bool available, bool installable)
    {
        var version = new Version(1 + offset, 0, 0);
        using var service = Create((_, _) => Task.FromResult(Release("v" + version, package, digest)));
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.Equal(version.ToString(), result.LatestVersion);
        Assert.Equal(available, result.UpdateAvailable);
        Assert.Equal(installable, result.CanInstall);
        Assert.False(result.CheckFailed);
    }

    [Theory]
    [InlineData("not-json", 200)]
    [InlineData("{\"tag_name\":\"invalid\",\"html_url\":\"https://github.com/eatyon/WinPilot\"}", 200)]
    [InlineData("{}", 200)]
    [InlineData("unavailable", 503)]
    public async Task InvalidReleaseAndHttpFailureReturnFailureWithoutThrowing(string content, int status)
    {
        using var service = Create((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(content) }));
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.True(result.CheckFailed);
        Assert.False(result.CanInstall);
        Assert.False(result.UpdateAvailable);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task ConcurrentChecksShareOneRequestAndCachedResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var service = Create(async (request, token) =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.NotEmpty(request.Headers.UserAgent);
            Interlocked.Increment(ref requests);
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return Release("v9999.0.0", true, true);
        });
        var first = service.CheckAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.CheckAsync(CancellationToken.None);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(results[0], results[1]);
        Assert.Same(results[0], await service.CheckAsync(CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CancelledRequestIsNotCachedAndNextCheckCanRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var service = Create(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Release("v9999.0.0", true, true);
        });
        using var cancellation = new CancellationTokenSource();
        var pending = service.CheckAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False((await service.CheckAsync(CancellationToken.None)).CheckFailed);
        Assert.Equal(2, calls);
    }

    private static UpdateCheckService Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(NullLogger<UpdateCheckService>.Instance, new HttpClient(new Handler(send)), currentVersion: "1.0.0");

    private static HttpResponseMessage Release(string tag, bool package, bool digest)
    {
        var assets = new List<object>();
        if (package) assets.Add(new { name = $"WinPilot-{tag}-win-x64.zip", browser_download_url = $"https://github.com/eatyon/WinPilot/releases/download/{tag}/WinPilot-{tag}-win-x64.zip", digest = digest ? "sha256:" + new string('a', 64) : null });
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { tag_name = tag, html_url = "https://github.com/eatyon/WinPilot/releases/" + tag, assets })) };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
