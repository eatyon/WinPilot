using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Features.Updates;
using WinPilot.Core;

namespace WinPilot.Test;

public sealed class UpdateInstallationLifetimeTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinPilot.UpdateLifetime.Test", Guid.NewGuid().ToString("N"));
    private readonly byte[] package = Encoding.UTF8.GetBytes("test update package");
    private string TemporaryDirectory => Path.Combine(root, "temp");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalReleasesStateGateBeforeWaitingForStoppingRegistration(bool asynchronous)
    {
        using var stopping = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposing = new ManualResetEventSlim();
        var handler = new Handler((_, _) => throw new InvalidOperationException("不应发起网络请求"));
        var service = Create(handler);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var gate = typeof(UpdateService).GetField("installStatusGate", flags)!.GetValue(service)!;
        var callbackAcquired = false;
        var callback = stopping.Token.Register(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("停止回调握手未解除");
            callbackAcquired = Monitor.TryEnter(gate);
            if (!callbackAcquired) return;
            try { typeof(UpdateService).GetMethod("RequestStop", flags)!.Invoke(service, null); }
            finally { Monitor.Exit(gate); }
        });
        typeof(UpdateService).GetField("stoppingRegistration", flags)!.SetValue(service, callback);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            disposing.Set();
            try
            {
                DisposeService(service, asynchronous);
                completed.TrySetResult();
            }
            catch (Exception exception) { completed.TrySetException(exception); }
        }) { IsBackground = true };
        var cancelled = Task.Run(stopping.Cancel);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            thread.Start();
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => thread.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(5)));
            var available = Monitor.TryEnter(gate);
            if (available) Monitor.Exit(gate);
            Task? concurrent = null;
            if (available)
            {
                concurrent = service.DisposeAsync().AsTask();
                Assert.Same(typeof(UpdateService).GetField("disposal", flags)!.GetValue(service), concurrent);
                Assert.False(concurrent.IsCompleted);
            }
            release.Set();
            await Task.WhenAll(cancelled, completed.Task, concurrent ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(available, "释放取消注册时仍持有停止回调所需的状态锁");
            Assert.True(callbackAcquired);
            Assert.True(handler.Disposed);
            Assert.Equal(1, handler.DisposeCount);
            await service.DisposeAsync();
        }
        finally
        {
            release.Set();
            await cancelled.WaitAsync(TimeSpan.FromSeconds(5));
            if (!thread.ThreadState.HasFlag(System.Threading.ThreadState.Unstarted)) await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task StoppingCancelsDownloadAndWaitsForCleanupBeforeDisposingClient()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DownloadStream(entered, cancelled, release);
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
            ? Release() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) }));
        var launches = 0;
        var service = Create(handler, start: _ => { launches++; return Process.GetCurrentProcess(); });
        Task? disposal = null;
        try
        {
            await Start(service);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = service.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposal.IsCompleted);
            Assert.False(handler.Disposed);
            Assert.False(source.Disposed);
            Assert.True(File.Exists(Path.Combine(Assert.Single(Directory.GetDirectories(TemporaryDirectory)), "package.zip")));
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(handler.Disposed);
            Assert.True(source.Disposed);
            Assert.Empty(Directory.GetDirectories(TemporaryDirectory));
            Assert.Equal(0, launches);
            Assert.Equal("Idle", service.GetInstallStatus().State);
            await service.DisposeAsync();
        }
        finally { release.TrySetResult(); await (disposal ?? service.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task HashMismatchCleansUnhandedDirectoryAndDoesNotLaunch()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new InstallationLogger();
        logger.OnMessage = message => { if (message.StartsWith("安装更新失败", StringComparison.Ordinal)) failed.TrySetResult(); };
        var launches = 0;
        await using var service = Create(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
            ? Release(new string('a', 64)) : Package())), logger: logger, start: _ => { launches++; return Process.GetCurrentProcess(); });
        await Start(service);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, launches);
        Assert.Equal("更新包校验失败", Assert.Single(logger.Errors).Message);
        Assert.Empty(Directory.GetDirectories(TemporaryDirectory));
    }

    [Fact]
    public async Task StopBeforeVerificationPassesCancellationAndDoesNotLaunch()
    {
        using var lifetime = new Lifetime();
        var logger = new InstallationLogger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.OnMessage = message =>
        {
            if (!message.StartsWith("更新包下载完成", StringComparison.Ordinal)) return;
            lifetime.StopApplication();
            entered.TrySetResult();
        };
        var launches = 0;
        await using var service = Create(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Release() : Package())),
            lifetime, logger, _ => { launches++; return Process.GetCurrentProcess(); });
        await Start(service);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, launches);
        Assert.Empty(Directory.GetDirectories(TemporaryDirectory));
        Assert.DoesNotContain(logger.Messages, message => message.StartsWith("更新包 SHA-256 校验通过", StringComparison.Ordinal));
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task StopAfterVerificationBeforeLaunchCommitDoesNotInvokeUpdater()
    {
        using var lifetime = new Lifetime();
        var logger = new InstallationLogger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.OnMessage = message =>
        {
            if (!message.StartsWith("更新包 SHA-256 校验通过", StringComparison.Ordinal)) return;
            lifetime.StopApplication();
            entered.TrySetResult();
        };
        var launches = 0;
        await using var service = Create(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Release() : Package())),
            lifetime, logger, _ => { launches++; return Process.GetCurrentProcess(); });
        await Start(service);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, launches);
        Assert.Equal("Idle", service.GetInstallStatus().State);
        Assert.False(HasHandedOff(service));
        Assert.Empty(Directory.GetDirectories(TemporaryDirectory));
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task SuccessfulHandoffRetainsDirectoryAndExitCanDisposeWithoutWaitingForItself()
    {
        using var lifetime = new Lifetime();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? directory = null;
        UpdateService? service = null;
        service = Create(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Release() : Package())),
            lifetime: lifetime,
            start: info =>
            {
                directory = info.WorkingDirectory;
                Assert.Equal("runas", info.Verb);
                Assert.True(info.UseShellExecute);
                Assert.Contains("--parent-pid 123", info.Arguments);
                Assert.Equal(package, File.ReadAllBytes(Path.Combine(directory, "package.zip")));
                return Process.GetCurrentProcess();
            }, exit: () =>
            {
                var token = ((CancellationTokenSource)typeof(UpdateService).GetField("stoppingCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!).Token;
                lifetime.StopApplication();
                service!.Dispose();
                Assert.False(token.IsCancellationRequested);
                exited.TrySetResult();
            });
        try
        {
            await Start(service);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Restarting", service.GetInstallStatus().State);
            Assert.True(Directory.Exists(directory));
            await service.DisposeAsync();
            Assert.True(Directory.Exists(directory));
        }
        finally { await service.DisposeAsync(); }
    }

    [Fact]
    public async Task ProductionConstructorReceivesHostStoppingSignalWithoutStartingInstallation()
    {
        using var lifetime = new Lifetime();
        var paths = PortableDataPaths.Create(root);
        await using var services = new ServiceCollection().AddLogging().AddSingleton(paths)
            .AddSingleton<IHostApplicationLifetime>(lifetime).AddSingleton<UpdateService>().BuildServiceProvider();
        var service = services.GetRequiredService<UpdateService>();
        var token = ((CancellationTokenSource)typeof(UpdateService).GetField("stoppingCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!).Token;
        lifetime.StopApplication();
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(token.IsCancellationRequested);
        Assert.Equal("Idle", service.GetInstallStatus().State);
    }

    [Fact]
    public async Task FailureAfterUpdaterCreationNeverDeletesHandedOffDirectory()
    {
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? directory = null;
        var logger = new InstallationLogger();
        await using var service = Create(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Release() : Package())),
            logger: logger, start: info => { directory = info.WorkingDirectory; created.TrySetResult(); return new Process(); });
        await Start(service);
        await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Failed", service.GetInstallStatus().State);
        Assert.Single(logger.Errors);
        Assert.True(File.Exists(Path.Combine(directory!, "package.zip")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(1223)]
    public async Task LaunchFailureCleansDirectory(int errorCode)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = Create(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Release() : Package())),
            start: _ => { entered.TrySetResult(); if (errorCode != 0) throw new System.ComponentModel.Win32Exception(errorCode); return null; });
        await Start(service);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Failed", service.GetInstallStatus().State);
        Assert.Empty(Directory.GetDirectories(TemporaryDirectory));
        Assert.False(HasHandedOff(service));
    }

    [Fact]
    public async Task StopRacingSuccessfulLaunchWaitsForResultAndPreservesDirectory()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        string? directory = null;
        await using var service = Create(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Release() : Package())),
            start: info =>
            {
                directory = info.WorkingDirectory;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("未解除启动握手");
                return Process.GetCurrentProcess();
            });
        Task? stopping = null;
        var preparation = PreparationToken(service);
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Start(service);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Verifying", (await Task.Run(service.GetInstallStatus).WaitAsync(TimeSpan.FromSeconds(5))).State);
            Assert.False(HasHandedOff(service));
            stopping = Task.Run(() =>
            {
                var pending = service.StopAsync();
                stopEntered.TrySetResult();
                return pending;
            });
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            Assert.False(preparation.IsCancellationRequested);
            Assert.True(Directory.Exists(directory));
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Directory.Exists(directory));
            Assert.True(HasHandedOff(service));
            Assert.False(preparation.IsCancellationRequested);
        }
        finally { release.Set(); if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(1223)]
    public async Task CommittedLaunchFailureDuringStopStillCleansDirectory(int errorCode)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var logger = new InstallationLogger();
        var launches = 0;
        await using var service = Create(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Release() : Package())),
            logger: logger, start: _ =>
            {
                launches++;
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                if (errorCode != 0) throw new System.ComponentModel.Win32Exception(errorCode);
                return null;
            });
        var preparation = PreparationToken(service);
        Task? stopping = null;
        try
        {
            await Start(service);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stopping = Task.Run(() =>
            {
                var pending = service.StopAsync();
                stopEntered.TrySetResult();
                return pending;
            });
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            Assert.False(preparation.IsCancellationRequested);
            Assert.False(HasHandedOff(service));
        }
        finally
        {
            release.Set();
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, launches);
        Assert.Equal("Failed", service.GetInstallStatus().State);
        Assert.False(HasHandedOff(service));
        Assert.Empty(Directory.GetDirectories(TemporaryDirectory));
        var failure = Assert.Single(logger.Errors);
        if (errorCode != 0) Assert.Equal(errorCode, Assert.IsType<System.ComponentModel.Win32Exception>(failure).NativeErrorCode);
    }

    private static CancellationToken PreparationToken(UpdateService service) =>
        ((CancellationTokenSource)typeof(UpdateService).GetField("stoppingCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!).Token;

    [Fact]
    public async Task FailedLaunchDoesNotLeaveNextPreparationCommitted()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloads = 0;
        var launches = 0;
        await using var service = Create(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Release();
            if (Interlocked.Increment(ref downloads) == 1) return Package();
            entered.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); await release.Task; throw; }
            return Package();
        }), start: _ => { launches++; return null; });
        Task? stopping = null;
        try
        {
            await Start(service);
            var first = (Task<bool>)typeof(UpdateService).GetField("installation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
            Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(Directory.GetDirectories(TemporaryDirectory));
            Assert.False(HasHandedOff(service));
            await Start(service);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = service.StopAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(PreparationToken(service).IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
            await (stopping ?? service.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, launches);
        Assert.Equal("Idle", service.GetInstallStatus().State);
        Assert.Empty(Directory.GetDirectories(TemporaryDirectory));
    }

    private static bool HasHandedOff(UpdateService service)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var gate = typeof(UpdateService).GetField("installStatusGate", flags)!.GetValue(service)!;
        lock (gate) return (bool)typeof(UpdateService).GetField("handedOff", flags)!.GetValue(service)!;
    }

    [Fact]
    public async Task RequestCancellationDoesNotCancelAcceptedInstallation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken downloadToken = default;
        await using var service = Create(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Release();
            downloadToken = token;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Package();
        }), exit: () => exited.TrySetResult());
        using var request = new CancellationTokenSource();
        try
        {
            await Start(service, request.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            request.Cancel();
            Assert.False(downloadToken.IsCancellationRequested);
            release.TrySetResult();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Restarting", service.GetInstallStatus().State);
        }
        finally { release.TrySetResult(); }
    }

    private static void DisposeService(UpdateService service, bool asynchronous)
    {
        if (asynchronous) service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else service.Dispose();
    }

    private UpdateService Create(Handler handler, IHostApplicationLifetime? lifetime = null, ILogger<UpdateService>? logger = null,
        Func<ProcessStartInfo, Process?>? start = null, Action? exit = null)
    {
        var application = Path.Combine(root, "app");
        Directory.CreateDirectory(application);
        Directory.CreateDirectory(TemporaryDirectory);
        File.WriteAllText(Path.Combine(application, "WinPilot.Updater.exe"), "test updater");
        return new(logger ?? NullLogger<UpdateService>.Instance, new HttpClient(handler), currentVersion: "1.0.0", lifetime: lifetime,
            applicationDirectory: application, temporaryDirectory: TemporaryDirectory, startUpdater: start ?? (_ => Process.GetCurrentProcess()), requestExit: exit ?? (() => { }));
    }

    private static async Task Start(UpdateService service, CancellationToken token = default)
    {
        var result = await service.CheckAsync(token);
        Assert.True(result.CanInstall);
        Assert.True(await service.TryStartInstallAsync(new(result.LatestVersion!, result.Source, result.Channel), 123, token));
    }

    private HttpResponseMessage Package() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
    private HttpResponseMessage Release(string? hash = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            tag_name = "v2.0.0", assets = new[] { new { name = "WinPilot-v2.0.0-win-x64.zip",
                browser_download_url = "https://github.com/eatyon/WinPilot/releases/download/v2.0.0/WinPilot-v2.0.0-win-x64.zip",
                digest = "sha256:" + (hash ?? Convert.ToHexString(SHA256.HashData(package))) } }
        }))
    };

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int DisposeCount { get; private set; }
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
        protected override void Dispose(bool disposing) { DisposeCount++; Disposed = true; base.Dispose(disposing); }
    }
    private sealed class DownloadStream(TaskCompletionSource entered, TaskCompletionSource cancelled, TaskCompletionSource release) : MemoryStream
    {
        internal bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); await release.Task; throw; }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }
    private sealed class InstallationLogger : ILogger<UpdateService>
    {
        internal Action<string>? OnMessage { get; set; }
        internal System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        internal System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Enqueue(message);
            if (exception is not null) Errors.Enqueue(exception);
            OnMessage?.Invoke(message);
        }
    }
}
