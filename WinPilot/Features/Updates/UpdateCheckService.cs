using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using WinPilot.Core;

namespace WinPilot.Features.Updates;

public sealed class UpdateCheckService : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly UpdateReleaseCatalog releases;
    private readonly string? configurationFile;
    private readonly SemanticVersion currentVersion;
    private readonly string currentDisplayVersion;
    private UpdateSettings settings;
    private readonly SemaphoreSlim gate = new(1, 1);
    private UpdateCheckResult? cachedResult;
    private UpdateRelease? cachedRelease;
    private DateTime cachedAtUtc;
    private readonly object installStatusGate = new();
    private UpdateInstallStatus installStatus = new("Idle", 0, null, "");
    private int installRunning;
    private readonly ILogger<UpdateCheckService> logger;

    public UpdateCheckService(ILogger<UpdateCheckService> logger, PortableDataPaths paths)
        : this(logger, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, paths.ConfigurationFile) { }

    internal UpdateCheckService(ILogger<UpdateCheckService> logger, HttpClient httpClient, string? configurationFile = null, string? currentVersion = null)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.configurationFile = configurationFile;
        settings = configurationFile is null ? new() : WinPilotSettings.LoadAndInitialize(configurationFile).Updates;
        currentDisplayVersion = currentVersion ?? WinPilotVersion.DisplayVersion;
        if (!SemanticVersion.TryParse(currentDisplayVersion, out var parsed)) throw new InvalidOperationException("当前程序版本不是有效的 SemVer");
        this.currentVersion = parsed;
        releases = new(httpClient);
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinPilot", WinPilotVersion.DisplayVersion));
    }

    public UpdateSettings GetSettings() => Volatile.Read(ref settings);

    public async Task<UpdateSettings> SaveSettingsAsync(UpdateSettings next, CancellationToken cancellationToken)
    {
        if (!next.IsValid) throw new ArgumentException("更新设置无效");
        if (Volatile.Read(ref installRunning) != 0) throw new InvalidOperationException("更新任务正在进行，无法修改更新设置");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref installRunning) != 0) throw new InvalidOperationException("更新任务正在进行，无法修改更新设置");
            if (configurationFile is not null) WinPilotSettings.Update(configurationFile, current => current with { Updates = next });
            Volatile.Write(ref settings, next);
            cachedResult = null;
            cachedRelease = null;
            logger.LogInformation("更新设置已保存，更新源：{Source}，版本通道：{Channel}", SourceName(next.Source), ChannelName(next.Channel));
            return next;
        }
        finally { gate.Release(); }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        => (await GetUpdateAsync(cancellationToken)).Result;

    private async Task<(UpdateCheckResult Result, UpdateRelease? Release)> GetUpdateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var selected = settings;
            if (cachedResult is not null && cachedResult.Source == selected.Source && cachedResult.Channel == selected.Channel
                && DateTime.UtcNow - cachedAtUtc < TimeSpan.FromMinutes(10)) return (cachedResult, cachedRelease);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                cachedRelease = await releases.GetAsync(selected, timeout.Token);
                var updateAvailable = cachedRelease is not null && cachedRelease.Version.CompareTo(currentVersion) > 0;
                cachedResult = new UpdateCheckResult(currentDisplayVersion, cachedRelease?.Version.Value, updateAvailable,
                    cachedRelease?.ReleaseUrl, updateAvailable && cachedRelease!.CanInstall, false, selected.Source, selected.Channel, cachedRelease?.ReleaseNotes ?? "");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "检查更新失败，更新源：{Source}，版本通道：{Channel}", SourceName(selected.Source), ChannelName(selected.Channel));
                cachedRelease = null;
                cachedResult = new UpdateCheckResult(currentDisplayVersion, null, false, null, false, true, selected.Source, selected.Channel, "");
            }
            cachedAtUtc = DateTime.UtcNow;
            if (!cachedResult.CheckFailed)
            {
                logger.LogDebug("更新检查完成，更新源：{Source}，版本通道：{Channel}，当前版本：{CurrentVersion}，最新版本：{LatestVersion}", SourceName(selected.Source), ChannelName(selected.Channel), cachedResult.CurrentVersion, cachedResult.LatestVersion ?? "暂无发布版本");
            }
            return (cachedResult, cachedRelease);
        }
        finally { gate.Release(); }
    }

    public UpdateInstallStatus GetInstallStatus()
    {
        lock (installStatusGate) return installStatus;
    }

    public async Task<bool> TryStartInstallAsync(int parentProcessId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Interlocked.CompareExchange(ref installRunning, 1, 0) != 0) return false;
            SetInstallStatus("Preparing", 0, null, "正在准备更新…");
            _ = Task.Run(() => DownloadAndLaunchAsync(parentProcessId));
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task DownloadAndLaunchAsync(int parentProcessId)
    {
        string? updateDirectory = null;
        try
        {
            logger.LogInformation("开始准备更新，主程序进程 ID：{ParentProcessId}", parentProcessId);
            var (result, release) = await GetUpdateAsync(CancellationToken.None);
            if (result.CheckFailed || !result.UpdateAvailable) throw new InvalidOperationException("当前没有可安装的更新");
            if (release is null || !release.CanInstall) throw new InvalidOperationException("此版本暂不支持在线更新，请前往版本发布页面手动下载");
            var updaterPath = Path.Combine(AppContext.BaseDirectory, "WinPilot.Updater.exe");
            if (!File.Exists(updaterPath)) throw new InvalidOperationException("当前安装缺少更新器，请手动下载最新版本");

            var package = release.Package!;
            var packageHash = package.Sha256!;
            var packageUrl = UpdateReleaseCatalog.CreateDownloadUri(result.Source, package.Url);
            updateDirectory = Path.Combine(Path.GetTempPath(), $"WinPilot-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(updateDirectory);
            var packagePath = Path.Combine(updateDirectory, "package.zip");
            logger.LogInformation("开始下载更新包，版本：{Version}，文件：{Package}，更新源：{Source}", result.LatestVersion, package.Name, SourceName(result.Source));
            await DownloadFileAsync(packageUrl, packagePath, (downloaded, total) => SetInstallStatus("Downloading", downloaded, total, "正在下载更新…"), CancellationToken.None);
            logger.LogInformation("更新包下载完成，版本：{Version}，文件大小：{Bytes} 字节", result.LatestVersion, new FileInfo(packagePath).Length);
            SetInstallStatus("Verifying", 0, null, "正在校验更新包…");
            if (!string.Equals(await ComputeSha256Async(packagePath, CancellationToken.None), packageHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新包校验失败");
            logger.LogInformation("更新包 SHA-256 校验通过，版本：{Version}", result.LatestVersion);
            var temporaryUpdaterPath = Path.Combine(updateDirectory, "WinPilot.Updater.exe");
            File.Copy(updaterPath, temporaryUpdaterPath, true);
            var arguments = $"--parent-pid {parentProcessId} --package-path \"{packagePath}\" --package-sha256 {packageHash} --target-dir \"{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}\" --cleanup-dir \"{updateDirectory}\"";
            using var updaterProcess = Process.Start(new ProcessStartInfo(temporaryUpdaterPath, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = updateDirectory });
            if (updaterProcess is null) throw new InvalidOperationException("无法启动更新器");
            logger.LogInformation("更新器已启动，目标版本：{Version}，更新器进程 ID：{UpdaterProcessId}", result.LatestVersion, updaterProcess.Id);
            SetInstallStatus("Restarting", 0, null, "更新器已启动，WinPilot 即将重启…");
            await Task.Delay(500);
            System.Windows.Forms.Application.Exit();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "安装更新失败，失败阶段：{Stage}", GetInstallStatus().State switch
            {
                "Preparing" => "准备更新",
                "Downloading" => "下载更新包",
                "Verifying" => "校验更新包及启动更新器",
                "Restarting" => "等待主程序退出",
                var state => state
            });
            if (!string.IsNullOrWhiteSpace(updateDirectory))
            {
                try { Directory.Delete(updateDirectory, true); }
                catch (Exception cleanupException) { logger.LogWarning(cleanupException, "清理更新临时目录失败"); }
            }
            SetInstallStatus("Failed", 0, null, "更新失败，请重试或前往版本发布页面手动下载");
            Interlocked.Exchange(ref installRunning, 0);
        }
    }

    private async Task DownloadFileAsync(Uri url, string destination, Action<long, long?> progress, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);
        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            progress(downloaded, response.Content.Headers.ContentLength);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
    }

    private void SetInstallStatus(string state, long downloadedBytes, long? totalBytes, string message)
    {
        lock (installStatusGate) installStatus = new UpdateInstallStatus(state, downloadedBytes, totalBytes, message);
    }

    private static string SourceName(UpdateSource source) => source == UpdateSource.Cnb ? "CNB" : "GitHub";
    private static string ChannelName(UpdateChannel channel) => channel == UpdateChannel.Preview ? "测试版（含正式版）" : "正式版";

    public void Dispose() { httpClient.Dispose(); gate.Dispose(); }
}

public sealed record UpdateCheckResult(string CurrentVersion, string? LatestVersion, bool UpdateAvailable, string? ReleaseUrl, bool CanInstall, bool CheckFailed, UpdateSource Source, UpdateChannel Channel, string ReleaseNotes);
public sealed record UpdateInstallStatus(string State, long DownloadedBytes, long? TotalBytes, string Message);
