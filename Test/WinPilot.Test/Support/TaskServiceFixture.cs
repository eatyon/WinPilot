using WinPilot.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Desktop;
using WinPilot.Core.Input;
using WinPilot.Core.Notifications;
using WinPilot.Core.Programs;
using WinPilot.Core.Tasks;
using WinPilot.Features.Desktop;
using WinPilot.Features.Notifications;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.Features.Programs;
using WinPilot.Features.Tasks;
using WinPilot.Features.Terminal;

namespace WinPilot.Test;

internal sealed class TaskServiceFixture : IAsyncDisposable
{
    private readonly PortableDataPaths paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "winpilot-task-command-tests", Guid.NewGuid().ToString("N")));
    internal PortableDataPaths Paths => paths;
    private readonly TerminalSessionManager terminals = new(NullLogger<TerminalSessionManager>.Instance);
    private readonly WindowsProcessEventListener processEvents = new(NullLogger<WindowsProcessEventListener>.Instance);
    internal TerminalSessionManager Terminals => terminals;
    private readonly Keyboard keyboard = new();
    internal IReadOnlyList<KeyboardKey>? SentKeys => keyboard.Keys;
    internal ProgramManager Programs { get; } = new();
    internal WinPilot.Features.Plugins.PluginTaskExtensionRegistry Extensions { get; } = new(new(), new(), new());
    internal TaskLogger Logger { get; } = new();
    internal TaskService Tasks { get; }
    internal TaskServiceFixture(INotificationService? notifications = null) => Tasks = new(paths, Programs, new ProgramTagService(paths),
        new KeyboardInputService(keyboard), terminals, new DesktopScreenshotService(new Screenshot()),
        notifications ?? new Notifications(), processEvents, Logger, Extensions);
    internal void Create(string name, bool enabled = true, bool delay = false) => Tasks.Create(new(name, enabled,
        new(TaskTriggerType.WinPilotStartup), delay
            ? [new(TaskActionType.Delay, DelaySeconds: 1), new(TaskActionType.StartProgram, ProgramPath: "app.exe")]
            : [new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
    internal CommandProcessor Processor() => new("Test", new(), NullLogger.Instance, tasks: Tasks);
    public async ValueTask DisposeAsync()
    {
        await Tasks.StopAsync(CancellationToken.None);
        Tasks.Dispose();
        processEvents.Dispose();
        await terminals.DisposeAsync();
        if (Directory.Exists(paths.ApplicationDirectory)) Directory.Delete(paths.ApplicationDirectory, true);
    }

    internal sealed class ProgramManager : IProgramManager
    {
        internal int Launches;
        internal System.Collections.Concurrent.ConcurrentQueue<ProgramLaunchRequest> Requests { get; } = new();
        internal bool FailFirst;
        internal TaskCompletionSource Launched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<RunningProgram> RunningPrograms { get; set; } = [];
        internal string? QueriedUserName { get; private set; }
        internal TaskCompletionSource<(IReadOnlyList<int> Ids, bool Force)> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Launch(ProgramLaunchRequest request)
        {
            Requests.Enqueue(request);
            var count = Interlocked.Increment(ref Launches);
            if (FailFirst && count == 1) throw new FileNotFoundException();
            Launched.TrySetResult();
        }
        public IReadOnlyList<InteractiveUser> GetInteractiveUsers() => [];
        public IReadOnlyList<RunningProgram> GetRunningPrograms(string userName)
        {
            QueriedUserName = userName;
            return RunningPrograms.Where(program => string.Equals(program.UserName, userName, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        public ProgramUsage GetUsage(IReadOnlyList<int> ids, CancellationToken token) => throw new NotSupportedException();
        public void Close(IReadOnlyList<int> ids, bool force) => Closed.TrySetResult((ids, force));
    }
    private sealed class Keyboard : IKeyboardInputSimulator
    {
        internal IReadOnlyList<KeyboardKey>? Keys { get; private set; }
        public void Send(IReadOnlyList<KeyboardKey> keys) => Keys = keys.ToArray();
    }
    internal sealed class TaskLogger : Microsoft.Extensions.Logging.ILogger<TaskService>
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        internal System.Collections.Concurrent.ConcurrentQueue<Exception> Exceptions { get; } = new();
        internal volatile bool ActionCancelled;
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
            if (exception is not null) Exceptions.Enqueue(exception);
            if (ActionCancelled) Cancelled.TrySetResult();
        }
    }
    private sealed class Screenshot : IDesktopScreenshotProvider { public DesktopScreenshot Capture() => throw new NotSupportedException(); }
    private sealed class Notifications : INotificationService
    {
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default,
            string? channel = null, bool ignoreChannelEnabled = false) => Task.FromResult<IReadOnlyList<NotificationChannelResult>>([]);
    }
}
