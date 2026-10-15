using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Features.Updates;

namespace WinPilot.Test;

public sealed class UpdateServiceTest
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
    public async Task InvalidReleaseAndHttpFailureAllowImmediateRetry(string content, int status)
    {
        var requests = 0;
        using var service = Create((_, _) => Task.FromResult(++requests == 1
            ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(content) }
            : Release("v2.0.0", true, true)));
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.True(result.CheckFailed);
        Assert.False(result.CanInstall);
        Assert.False(result.UpdateAvailable);
        Assert.Null(result.LatestVersion);
        var retry = await service.CheckAsync(CancellationToken.None);
        Assert.False(retry.CheckFailed);
        Assert.Equal("2.0.0", retry.LatestVersion);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ConcurrentChecksAreSerializedAndEachCheckReadsLatestRelease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var service = Create(async (request, token) =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.NotEmpty(request.Headers.UserAgent);
            var index = Interlocked.Increment(ref requests);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Release($"v{index + 1}.0.0", true, true);
        });
        var first = service.CheckAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.CheckAsync(CancellationToken.None);
        Assert.Equal(1, requests);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("2.0.0", results[0].LatestVersion);
        Assert.Equal("3.0.0", results[1].LatestVersion);
        Assert.Equal("4.0.0", (await service.CheckAsync(CancellationToken.None)).LatestVersion);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task InstallWithoutPackageDigestFailsBeforeDownloading()
    {
        var requests = 0;
        using var service = Create((request, _) =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            requests++;
            return Task.FromResult(Release("v2.0.0", true, false));
        });
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.True(result.UpdateAvailable);
        Assert.False(result.CanInstall);
        Assert.True(await service.TryStartInstallAsync(new("2.0.0", UpdateSource.GitHub, UpdateChannel.Stable), 0, CancellationToken.None));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (service.GetInstallStatus().State != "Failed") await Task.Delay(10, timeout.Token);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("1.0.0", UpdateSource.GitHub, UpdateChannel.Stable)]
    [InlineData("2.0.0", UpdateSource.Cnb, UpdateChannel.Stable)]
    [InlineData("2.0.0", UpdateSource.GitHub, UpdateChannel.Preview)]
    public async Task ChangedInstallTargetIsRejectedWithoutStartingUpdate(string version, UpdateSource source, UpdateChannel channel)
    {
        using var service = Create((_, _) => Task.FromResult(Release("v2.0.0", true, true)));
        await service.CheckAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryStartInstallAsync(new(version, source, channel), 0, CancellationToken.None));
        Assert.Equal("更新信息已变化，请重新检查更新", error.Message);
        Assert.Equal("Idle", service.GetInstallStatus().State);
    }

    [Fact]
    public async Task ChangedSettingsRequireAnotherCheckBeforeInstalling()
    {
        using var service = Create((_, _) => Task.FromResult(Release("v2.0.0", true, true)));
        await service.CheckAsync(CancellationToken.None);
        await service.SaveSettingsAsync(new() { Source = UpdateSource.Cnb }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryStartInstallAsync(new("2.0.0", UpdateSource.GitHub, UpdateChannel.Stable), 0, CancellationToken.None));
        Assert.Equal("Idle", service.GetInstallStatus().State);
    }

    [Fact]
    public async Task CancelledCheckAllowsNextCheckToRetry()
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

    private static UpdateService Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(NullLogger<UpdateService>.Instance, new HttpClient(new Handler(send)), currentVersion: "1.0.0");

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
