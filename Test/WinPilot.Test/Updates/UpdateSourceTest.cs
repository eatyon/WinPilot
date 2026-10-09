using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Features.Updates;

namespace WinPilot.Test;

public sealed class UpdateSourceTest
{
    [Theory]
    [InlineData(UpdateSource.GitHub, "## 改进\n- 更新说明")]
    [InlineData(UpdateSource.Cnb, "## 改进\n- 更新说明")]
    [InlineData(UpdateSource.GitHub, null)]
    [InlineData(UpdateSource.Cnb, null)]
    public async Task ReleaseNotesFollowSelectedSourceAndCheckCache(UpdateSource source, string? notes)
    {
        var requests = 0;
        using var service = Create((request, _) =>
        {
            requests++;
            Assert.Equal(source == UpdateSource.Cnb ? "cnb.cool" : "api.github.com", request.RequestUri!.Host);
            return Task.FromResult(Json(Release("v2.0.0", source, notes: notes)));
        });
        await service.SaveSettingsAsync(new() { Source = source }, CancellationToken.None);
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.Equal(notes ?? "", result.ReleaseNotes);
        Assert.Same(result, await service.CheckAsync(CancellationToken.None));
        Assert.Equal(1, requests);
        await service.SaveSettingsAsync(new() { Source = source }, CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(UpdateSource.GitHub)]
    [InlineData(UpdateSource.Cnb)]
    public async Task PackageSha256UsesSelectedSourceAndNormalizesHexCase(UpdateSource source)
    {
        var hash = new string('A', 64);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(ReleaseWithDigest(source, "sha256:" + hash, "sha256", hash)))));
        var release = await new UpdateReleaseCatalog(client).GetAsync(new() { Source = source }, CancellationToken.None);
        Assert.NotNull(release);
        Assert.True(release.CanInstall);
        Assert.Equal(hash.ToLowerInvariant(), release.Package!.Sha256);
    }

    [Theory]
    [InlineData(UpdateSource.GitHub, null, "sha256", null)]
    [InlineData(UpdateSource.GitHub, "", "sha256", null)]
    [InlineData(UpdateSource.GitHub, "sha256:abc", "sha256", null)]
    [InlineData(UpdateSource.GitHub, "sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sha256", null)]
    [InlineData(UpdateSource.GitHub, "sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", "sha256", null)]
    [InlineData(UpdateSource.GitHub, "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ", "sha256", null)]
    [InlineData(UpdateSource.Cnb, null, null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(UpdateSource.Cnb, null, "sha512", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(UpdateSource.Cnb, null, "sha256", null)]
    [InlineData(UpdateSource.Cnb, null, "sha256", "")]
    [InlineData(UpdateSource.Cnb, null, "sha256", "abc")]
    [InlineData(UpdateSource.Cnb, null, "sha256", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData(UpdateSource.Cnb, null, "sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ")]
    public async Task InvalidPackageDigestAllowsDisplayButNotInstallOrFallback(UpdateSource source, string? digest, string? algorithm, string? hash)
    {
        var requests = 0;
        using var service = Create((request, _) =>
        {
            requests++;
            Assert.Equal(source == UpdateSource.GitHub ? "api.github.com" : "cnb.cool", request.RequestUri!.Host);
            return Task.FromResult(Json(ReleaseWithDigest(source, digest, algorithm, hash)));
        });
        await service.SaveSettingsAsync(new() { Source = source }, CancellationToken.None);
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.True(result.UpdateAvailable);
        Assert.False(result.CanInstall);
        Assert.False(result.CheckFailed);
        Assert.Equal(source, result.Source);
        Assert.NotNull(result.ReleaseUrl);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(UpdateSource.GitHub, "api.github.com", "/repos/eatyon/WinPilot/releases/latest")]
    [InlineData(UpdateSource.Cnb, "cnb.cool", "/asdbr/WinPilot/-/releases/latest")]
    public async Task StableUsesSelectedLatestEndpoint(UpdateSource source, string host, string path)
    {
        using var service = Create((request, _) =>
        {
            Assert.Equal(host, request.RequestUri!.Host);
            Assert.Equal(path, request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == (source == UpdateSource.Cnb ? "application/vnd.cnb.api+json" : "application/vnd.github+json"));
            return Task.FromResult(Json(Release("v2.0.0", source)));
        });
        await service.SaveSettingsAsync(new() { Source = source }, CancellationToken.None);
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.Equal(source, result.Source);
        Assert.Equal(UpdateChannel.Stable, result.Channel);
        Assert.True(result.CanInstall);
        Assert.Contains(source == UpdateSource.Cnb ? "cnb.cool/asdbr/WinPilot/-/releases/tag/" : "github.com/eatyon/WinPilot/releases/tag/", result.ReleaseUrl);
    }

    [Theory]
    [InlineData(UpdateSource.GitHub)]
    [InlineData(UpdateSource.Cnb)]
    public async Task PreviewReadsAllPagesAndChoosesHighestNotFirst(UpdateSource source)
    {
        var requests = 0;
        using var service = Create((request, _) =>
        {
            var page = Interlocked.Increment(ref requests);
            Assert.Equal(source == UpdateSource.Cnb ? "cnb.cool" : "api.github.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == (source == UpdateSource.Cnb ? "application/vnd.cnb.api+json" : "application/vnd.github+json"));
            Assert.Contains(source == UpdateSource.GitHub ? "per_page=100" : "page_size=100", request.RequestUri!.Query);
            Assert.Contains($"page={page}", request.RequestUri.Query);
            if (page == 1)
            {
                var releases = Enumerable.Range(0, 100).Select(_ => Release("v2.0.0-beta.2", source)).ToArray();
                releases[0] = Release("v99.0.0", source, draft: true);
                releases[1] = Release("v2.0.0-rc.1", source, prerelease: true);
                return Task.FromResult(Json(releases));
            }
            return Task.FromResult(Json(new[] { Release("v2.0.0-beta.10", source, prerelease: true), Release("v2.0.0", source) }));
        });
        await service.SaveSettingsAsync(new() { Source = source, Channel = UpdateChannel.Preview }, CancellationToken.None);
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.Equal("2.0.0", result.LatestVersion);
        Assert.True(result.CanInstall);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(true, false, "v2.0.0")]
    [InlineData(false, true, "v2.0.0")]
    [InlineData(false, false, "v2.0.0-beta.1")]
    public async Task StableRejectsDraftAndPrerelease(bool draft, bool prerelease, string tag)
    {
        using var service = Create((_, _) => Task.FromResult(Json(Release(tag, UpdateSource.GitHub, draft, prerelease))));
        Assert.True((await service.CheckAsync(CancellationToken.None)).CheckFailed);
    }

    [Theory]
    [InlineData("1.0.0-beta.2", "v1.0.0-beta.10", true)]
    [InlineData("1.0.0-rc.1", "v1.0.0", true)]
    [InlineData("1.0.0+build.2", "v1.0.0+build.3", false)]
    [InlineData("2.0.0", "v1.0.0", false)]
    public async Task PreviewDoesNotDowngradeAndUsesInformationalVersion(string current, string latest, bool available)
    {
        using var service = Create((_, _) => Task.FromResult(Json(new[] { Release(latest, UpdateSource.GitHub) })), currentVersion: current);
        await service.SaveSettingsAsync(new() { Channel = UpdateChannel.Preview }, CancellationToken.None);
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.Equal(available, result.UpdateAvailable);
        Assert.Equal(available, result.CanInstall);
    }

    [Fact]
    public async Task SettingsInvalidateCacheAndFailuresNeverSwitchSource()
    {
        var hosts = new List<string>();
        using var service = Create((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(request.RequestUri.Host == "api.github.com" ? Json(Release("v2.0.0", UpdateSource.GitHub)) : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        Assert.True((await service.CheckAsync(CancellationToken.None)).CanInstall);
        await service.SaveSettingsAsync(new() { Source = UpdateSource.Cnb }, CancellationToken.None);
        var failure = await service.CheckAsync(CancellationToken.None);
        Assert.True(failure.CheckFailed);
        Assert.Equal(UpdateSource.Cnb, failure.Source);
        Assert.False(failure.CanInstall);
        Assert.Equal(new[] { "api.github.com", "cnb.cool" }, hosts);
        await service.SaveSettingsAsync(new(), CancellationToken.None);
        Assert.True((await service.CheckAsync(CancellationToken.None)).CanInstall);
        Assert.Equal(3, hosts.Count);
    }

    [Fact]
    public async Task FailedLaterPageDoesNotReturnPartialHighestVersion()
    {
        var requests = 0;
        using var service = Create((_, _) => Task.FromResult(++requests == 1
            ? Json(Enumerable.Range(0, 100).Select(_ => Release("v2.0.0", UpdateSource.GitHub)).ToArray())
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await service.SaveSettingsAsync(new() { Channel = UpdateChannel.Preview }, CancellationToken.None);
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.True(result.CheckFailed);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task WrongAssetNameOrSourceAllowsDisplayButNotInstall()
    {
        var release = new
        {
            tag_name = "v2.0.0",
            assets = new[] {
                new { name = "WinPilot-v2.0.0-win-x64.zip", browser_download_url = "https://github.com/other/WinPilot/releases/download/v2.0.0/WinPilot-v2.0.0-win-x64.zip", digest = "sha256:" + new string('a', 64) }
            }
        };
        using var service = Create((_, _) => Task.FromResult(Json(release)));
        var result = await service.CheckAsync(CancellationToken.None);
        Assert.True(result.UpdateAvailable);
        Assert.False(result.CanInstall);
    }

    [Theory]
    [InlineData(UpdateSource.GitHub, "http://github.com/eatyon/package.zip")]
    [InlineData(UpdateSource.GitHub, "https://github.com.example.com/package.zip")]
    [InlineData(UpdateSource.GitHub, "https://example.com/package.zip")]
    [InlineData(UpdateSource.GitHub, "/package.zip")]
    [InlineData(UpdateSource.GitHub, "https://cnb.cool/asdbr/WinPilot/-/releases/download/v1/file")]
    [InlineData(UpdateSource.Cnb, "https://github.com/eatyon/WinPilot/releases/download/v1/file")]
    [InlineData(UpdateSource.Cnb, "https://cnb.cool.evil.com/asdbr/WinPilot/-/releases/download/v1/file")]
    [InlineData(UpdateSource.Cnb, "https://cnb.cool/other/WinPilot/-/releases/download/v1/file")]
    [InlineData(UpdateSource.Cnb, "http://cnb.cool/asdbr/WinPilot/-/releases/download/v1/file")]
    public void DownloadsAreRestrictedToSelectedRepository(UpdateSource source, string url)
        => Assert.Throws<InvalidOperationException>(() => UpdateReleaseCatalog.CreateDownloadUri(source, url));

    [Theory]
    [InlineData("cnb.cool", "WinPilot-v2.0.0-win-x64.zip")]
    [InlineData("api.cnb.cool", "WinPilot-v2.0.0-win-x64.zip")]
    public void CnbDownloadsUsePublicHost(string host, string fileName)
    {
        var uri = UpdateReleaseCatalog.CreateDownloadUri(UpdateSource.Cnb, $"https://{host}/asdbr/WinPilot/-/releases/download/v2.0.0/{fileName}");
        Assert.Equal($"https://cnb.cool/asdbr/WinPilot/-/releases/download/v2.0.0/{fileName}", uri.AbsoluteUri);
    }

    [Fact]
    public async Task SettingsPersistWithoutReplacingOtherSections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "winpilot-update-settings", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "config.json");
        try
        {
            WinPilotSettings.Update(file, current => current with { Port = 9555, SafeModeEnabled = true });
            using (var service = Create((_, _) => Task.FromResult(Json(Release("v2.0.0", UpdateSource.Cnb))), file))
            {
                await service.SaveSettingsAsync(new() { Source = UpdateSource.Cnb, Channel = UpdateChannel.Preview }, CancellationToken.None);
                await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSettingsAsync(new() { Source = (UpdateSource)99 }, CancellationToken.None));
            }
            using var reloaded = Create((_, _) => throw new InvalidOperationException(), file);
            Assert.Equal(UpdateSource.Cnb, reloaded.GetSettings().Source);
            Assert.Equal(UpdateChannel.Preview, reloaded.GetSettings().Channel);
            var stored = WinPilotSettings.LoadAndInitialize(file);
            Assert.Equal(9555, stored.Port);
            Assert.True(stored.SafeModeEnabled);
            Assert.Equal("", stored.Notifications.QQBotClientSecret);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedSaveKeepsPersistedSettingsAndCheckCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "winpilot-update-save-failure", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "config.json");
        try
        {
            using var service = Create((_, _) => Task.FromResult(Json(Release("v2.0.0", UpdateSource.GitHub))), file);
            var original = File.ReadAllText(file);
            var cached = await service.CheckAsync(CancellationToken.None);
            Directory.CreateDirectory(file + ".tmp");
            await Assert.ThrowsAnyAsync<Exception>(() => service.SaveSettingsAsync(new() { Source = UpdateSource.Cnb }, CancellationToken.None));
            Assert.Equal(UpdateSource.GitHub, service.GetSettings().Source);
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Same(cached, await service.CheckAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InstallBlocksSettingsUntilFailureAndIsIndependentOfRequestToken()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var cancellation = new CancellationTokenSource();
        Assert.True(await service.TryStartInstallAsync(0, cancellation.Token));
        cancellation.Cancel();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var save = service.SaveSettingsAsync(new() { Source = UpdateSource.Cnb }, CancellationToken.None);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => save);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (service.GetInstallStatus().State != "Failed") await Task.Delay(10, timeout.Token);
        await service.SaveSettingsAsync(new() { Source = UpdateSource.Cnb }, CancellationToken.None);
        Assert.Equal(UpdateSource.Cnb, service.GetSettings().Source);
    }

    private static UpdateCheckService Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, string? file = null, string currentVersion = "1.0.0")
        => new(NullLogger<UpdateCheckService>.Instance, new HttpClient(new Handler(send)), file, currentVersion);

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };

    private static object ReleaseWithDigest(UpdateSource source, string? digest, string? algorithm, string? hash)
    {
        var prefix = source == UpdateSource.GitHub ? "https://github.com/eatyon/WinPilot/releases/download/" : "https://cnb.cool/asdbr/WinPilot/-/releases/download/";
        return new {
            tag_name = "v2.0.0",
            assets = new[] {
                new { name = "WinPilot-v2.0.0-win-x64.zip", browser_download_url = prefix + "v2.0.0/WinPilot-v2.0.0-win-x64.zip",
                    digest = source == UpdateSource.GitHub ? digest : "sha256:" + new string('b', 64),
                    hash_algo = source == UpdateSource.Cnb ? algorithm : "sha256",
                    hash_value = source == UpdateSource.Cnb ? hash : new string('b', 64) }
            }
        };
    }

    private static object Release(string tag, UpdateSource source, bool draft = false, bool prerelease = false, string? notes = null)
    {
        var prefix = source == UpdateSource.GitHub ? "https://github.com/eatyon/WinPilot/releases/download/" : "https://cnb.cool/asdbr/WinPilot/-/releases/download/";
        return new {
            tag_name = tag, draft, prerelease, body = notes,
            assets = new[] {
                new { name = $"WinPilot-{tag}-win-x64.zip", browser_download_url = $"{prefix}{tag}/WinPilot-{tag}-win-x64.zip",
                    digest = source == UpdateSource.GitHub ? "sha256:" + new string('a', 64) : null,
                    hash_algo = source == UpdateSource.Cnb ? "sha256" : null,
                    hash_value = source == UpdateSource.Cnb ? new string('a', 64) : null }
            }
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
