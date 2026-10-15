using System.Collections.Concurrent;
using WinPilot.Windows;

namespace WinPilot.Features.Terminal;

public sealed class TerminalSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, TerminalSession> sessions = new();
    public const int MaxSessionsPerConnection = 20;
    private readonly SemaphoreSlim creationLock = new(1, 1);
    private readonly ILogger<TerminalSessionManager> logger;
    private bool disposed;

    internal int ActiveSessionCount => sessions.Count;

    public TerminalSessionManager(ILogger<TerminalSessionManager> logger) => this.logger = logger;

    public async Task<TerminalSessionInfo> CreateAsync(Guid connectionId, string shell, string? workingDirectory, Func<TerminalEvent, Task> publish, CancellationToken cancellationToken)
    {
        var type = ResolveShell(shell);
        var directory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("工作目录不存在");
        cancellationToken.ThrowIfCancellationRequested();
        await creationLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessions.Values.Count(session => session.ConnectionId == connectionId) >= MaxSessionsPerConnection)
                throw new InvalidOperationException($"已达到终端数量上限（{MaxSessionsPerConnection}）");
            var process = ConPtyTerminalProcess.Start(type, directory);
            var session = new TerminalSession(Guid.NewGuid(), connectionId, process, publish, RemoveAsync, logger);
            if (!sessions.TryAdd(session.Id, session))
            {
                await session.DisposeAsync();
                throw new InvalidOperationException("无法创建终端会话");
            }
            if (cancellationToken.IsCancellationRequested)
            {
                await session.CloseAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            session.Start();
            return new TerminalSessionInfo(session.Id, type, directory, DateTimeOffset.UtcNow);
        }
        finally { creationLock.Release(); }
    }

    public async Task<ShellCommandResult> ExecuteTaskCommandAsync(string shell, string command, CancellationToken cancellationToken)
    {
        var type = ResolveShell(shell);
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("请输入 Shell 命令");
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        using var waiter = new TaskShellOutputWaiter(type, command);
        var process = ConPtyTerminalProcess.Start(type, directory);
        var session = new TerminalSession(Guid.NewGuid(), Guid.Empty, process, item =>
        {
            waiter.Observe(item);
            return Task.CompletedTask;
        }, _ => Task.CompletedTask, logger);
        try
        {
            session.Start();
            await session.WriteAsync(waiter.WrappedCommand);
            return await waiter.WaitAsync(cancellationToken);
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    public Task InputAsync(Guid connectionId, Guid id, string data) => Get(connectionId, id).WriteAsync(data);

    public Task InterruptAsync(Guid connectionId, Guid id) => Get(connectionId, id).InterruptAsync();

    public Task ResizeAsync(Guid connectionId, Guid id, int columns, int rows)
    {
        Get(connectionId, id).Resize(columns, rows);
        return Task.CompletedTask;
    }

    public Task CloseAsync(Guid connectionId, Guid id) => Get(connectionId, id).CloseAsync();

    public async Task CloseConnectionAsync(Guid connectionId)
    {
        foreach (var session in sessions.Values.Where(s => s.ConnectionId == connectionId).ToArray())
            await session.CloseAsync();
    }

    public Task CloseAllAsync() => CloseSessionsAsync(sessions.Values.ToArray());

    internal Func<Task> CaptureCloseOperation()
    {
        var targets = sessions.Values.ToArray();
        return () => CloseSessionsAsync(targets);
    }

    private static async Task CloseSessionsAsync(TerminalSession[] targets)
    {
        foreach (var session in targets)
            await session.CloseAsync();
    }

    private TerminalSession Get(Guid connectionId, Guid id) => sessions.TryGetValue(id, out var session) && session.ConnectionId == connectionId ? session : throw new InvalidOperationException("终端会话不存在");

    private Task RemoveAsync(TerminalSession session)
    {
        sessions.TryRemove(session.Id, out _);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await creationLock.WaitAsync();
        try { disposed = true; }
        finally { creationLock.Release(); }
        await CloseAllAsync();
    }

    private static string ResolveShell(string shell) => shell.Equals("cmd", StringComparison.OrdinalIgnoreCase) ? "cmd" : shell.Equals("powershell", StringComparison.OrdinalIgnoreCase) ? "powershell" : throw new InvalidOperationException("不支持的终端类型");
}
