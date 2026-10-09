using WinPilot.Core;
using WinPilot.Core.Input;
using WinPilot.Core.Programs;
using WinPilot.Features.Desktop;
using WinPilot.Features.Plugins;
using WinPilot.Features.Terminal;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.PluginContract;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;

namespace WinPilot.Test;

public sealed class PluginHostActionApiTest
{
    [Fact]
    public async Task StartProgram_HonorsElevationAndSilentParameters()
    {
        var programs = new RecordingProgramManager();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(programs, new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals, new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);

        var result = await api.ExecuteAsync(new(PluginHostActionType.StartProgram, new Dictionary<string, object?>
        {
            ["programPath"] = "TestProgram.exe",
            ["arguments"] = "--test",
            ["workingDirectory"] = "C:\\TestProgram",
            ["runElevated"] = true,
            ["silent"] = false
        }));

        Assert.True(result.Succeeded);
        Assert.NotNull(programs.LastLaunch);
        Assert.True(programs.LastLaunch.RunElevated);
        Assert.False(programs.LastLaunch.Silent);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("qqbot", true)]
    public async Task SendNotification_RoutesThroughNotificationService(string? channel, bool ignoreEnabled)
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(new RecordingProgramManager(), new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals, notifications, NullLogger<PluginHostActionApi>.Instance);

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["content"] = "签到完成", ["channel"] = channel
        }));

        Assert.True(result.Succeeded);
        Assert.Equal("签到完成", notifications.Message?.Content);
        Assert.Equal("", notifications.Message?.Title);
        Assert.Equal(channel, notifications.Channel);
        Assert.Equal(ignoreEnabled, notifications.IgnoreEnabled);
    }

    [Fact]
    public async Task SendNotification_RejectsEmptyMessage()
    {
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(new RecordingProgramManager(), new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals, new RecordingNotificationService(), NullLogger<PluginHostActionApi>.Instance);

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>()));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SendNotification_WritesImageForDeliveryAndRemovesTemporaryFile()
    {
        var notifications = new RecordingNotificationService();
        await using var terminals = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var api = new PluginHostActionApi(new RecordingProgramManager(), new KeyboardInputService(new NoOpKeyboardInputSimulator()), terminals, notifications, NullLogger<PluginHostActionApi>.Instance);
        var image = new byte[] { 137, 80, 78, 71 };

        var result = await api.ExecuteAsync(new(PluginHostActionType.SendNotification, new Dictionary<string, object?>
        {
            ["image"] = new PluginImageContent(image, "image/png")
        }));

        Assert.True(result.Succeeded);
        Assert.Equal(image, notifications.ImageBytes);
        Assert.NotNull(notifications.Message?.ImagePath);
        Assert.False(File.Exists(notifications.Message.ImagePath));
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public NotificationMessage? Message { get; private set; }
        public string? Channel { get; private set; }
        public bool IgnoreEnabled { get; private set; }
        public byte[]? ImageBytes { get; private set; }
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default, string? channel = null, bool ignoreChannelEnabled = false)
        {
            Message = message;
            Channel = channel;
            IgnoreEnabled = ignoreChannelEnabled;
            ImageBytes = message.ImagePath is null ? null : File.ReadAllBytes(message.ImagePath);
            return Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new("Test", true)]);
        }
    }

    private sealed class RecordingProgramManager : IProgramManager
    {
        public ProgramLaunchRequest? LastLaunch { get; private set; }
        public IReadOnlyList<InteractiveUser> GetInteractiveUsers() => [];
        public IReadOnlyList<RunningProgram> GetRunningPrograms(string userName) => [];
        public ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Close(IReadOnlyList<int> processIds, bool force) => throw new NotSupportedException();
        public void Launch(ProgramLaunchRequest request) => LastLaunch = request;
    }

    private sealed class NoOpKeyboardInputSimulator : IKeyboardInputSimulator
    {
        public void Send(IReadOnlyList<KeyboardKey> keys) { }
    }

}
