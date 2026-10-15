using WinPilot.Core;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core.Desktop;
using WinPilot.Core.Input;
using WinPilot.Core.Notifications;
using WinPilot.Core.Programs;
using WinPilot.Core.Tasks;
using WinPilot.Features.Desktop;
using WinPilot.Features.Notifications;
using WinPilot.Features.Programs;
using WinPilot.Features.Tasks;
using WinPilot.Features.Terminal;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class DesktopImageVariableTest
{
    [Fact]
    public async Task DesktopImageVariableCapturesOnceSendsOnlyImageAndDeletesTemporaryFile()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), $"WinPilot-desktop-image-{Guid.NewGuid():N}");
        Directory.CreateDirectory(applicationDirectory);
        var screenshotBytes = new byte[] { 1, 2, 3, 4 };
        var screenshotProvider = new RecordingScreenshotProvider(new(DateTimeOffset.UtcNow, screenshotBytes, "image/jpeg"));
        var notificationService = new RecordingNotificationService();
        Exception? failure = null;
        try
        {
            await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
            using var processEvents = new WindowsProcessEventListener(NullLogger<WindowsProcessEventListener>.Instance);
            using var service = new TaskService(
                PortableDataPaths.Create(applicationDirectory),
                new NoOpProgramManager(),
                new ProgramTagService(PortableDataPaths.Create(applicationDirectory)),
                new KeyboardInputService(new NoOpKeyboardInputSimulator()),
                terminals,
                new DesktopScreenshotService(screenshotProvider),
                notificationService,
                processEvents,
                NullLogger<TaskService>.Instance,
                new(new(), new(), new()));
            try
            {
                service.Create(new("desktop-image", true, new(TaskTriggerType.WinPilotStartup),
                    [new(TaskActionType.SendNotification, NotificationContent: "&screenshot")]));

                service.RunNow("desktop-image");
                var sent = await notificationService.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(1, screenshotProvider.CaptureCount);
                Assert.Equal(string.Empty, sent.Message.Content);
                Assert.NotNull(sent.Message.ImagePath);
                Assert.Equal(screenshotBytes, sent.ImageBytes);
                await WaitUntilAsync(() => !File.Exists(sent.Message.ImagePath), TimeSpan.FromSeconds(5));
            }
            finally
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await service.StopAsync(cancellation.Token);
            }
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            try { Directory.Delete(applicationDirectory, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var cleanupFailure = new IOException($"清理桌面图片测试目录失败：{applicationDirectory}", exception);
                if (failure is not null) throw new AggregateException(failure, cleanupFailure);
                throw cleanupFailure;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待临时图片删除超时");
            await Task.Delay(50);
        }
    }

    private sealed class RecordingScreenshotProvider(DesktopScreenshot screenshot) : IDesktopScreenshotProvider
    {
        public int CaptureCount { get; private set; }
        public DesktopScreenshot Capture()
        {
            CaptureCount++;
            return screenshot;
        }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public TaskCompletionSource<(NotificationMessage Message, byte[] ImageBytes)> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default, string? channel = null, bool ignoreChannelEnabled = false)
        {
            var imageBytes = message.ImagePath is null ? [] : await File.ReadAllBytesAsync(message.ImagePath, cancellationToken);
            Sent.TrySetResult((message, imageBytes));
            return [new("Test", true)];
        }
    }

    private sealed class NoOpProgramManager : IProgramManager
    {
        public IReadOnlyList<InteractiveUser> GetInteractiveUsers() => [];
        public IReadOnlyList<RunningProgram> GetRunningPrograms(string userName) => [];
        public ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Close(IReadOnlyList<int> processIds, bool force) => throw new NotSupportedException();
        public void Launch(ProgramLaunchRequest request) => throw new NotSupportedException();
    }

    private sealed class NoOpKeyboardInputSimulator : IKeyboardInputSimulator
    {
        public void Send(IReadOnlyList<KeyboardKey> keys) { }
    }
}
