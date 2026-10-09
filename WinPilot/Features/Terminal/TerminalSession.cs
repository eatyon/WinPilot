using System.Text;

namespace WinPilot.Features.Terminal;

internal sealed class TerminalSession : IAsyncDisposable
{
    private readonly ConPtyTerminalProcess process;
    private readonly Func<TerminalEvent, Task> publish;
    private readonly Func<TerminalSession, Task> remove;
    private readonly ILogger logger;
    private readonly CancellationTokenSource cancellation = new();
    private readonly StreamWriter input;
    private Task? outputTask;
    private Task? watchTask;
    private int closed;
    private readonly TaskCompletionSource closeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid Id { get; }

    public Guid ConnectionId { get; }

    public TerminalSession(Guid id, Guid connectionId, ConPtyTerminalProcess process, Func<TerminalEvent, Task> publish, Func<TerminalSession, Task> remove, ILogger logger)
    {
        Id = id;
        ConnectionId = connectionId;
        this.process = process;
        this.publish = publish;
        this.remove = remove;
        this.logger = logger;
        input = new StreamWriter(process.Input, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public void Start()
    {
        outputTask = ReadAsync(process.Output, "stdout");
        watchTask = WatchAsync();
    }

    public async Task WriteAsync(string data)
    {
        if (Volatile.Read(ref closed) != 0)
            return;
        await input.WriteAsync(data.AsMemory(), cancellation.Token);
        await input.FlushAsync(cancellation.Token);
    }

    public Task InterruptAsync() => WriteAsync("\x03");

    public void Resize(int columns, int rows)
    {
        if (columns is < 1 or > short.MaxValue || rows is < 1 or > short.MaxValue)
            throw new ArgumentOutOfRangeException();
        process.Resize((short)columns, (short)rows);
    }

    private async Task ReadAsync(Stream stream, string kind)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[4096];
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token);
                if (read == 0)
                    break;
                await publish(new TerminalEvent("output", Id, kind, new string (buffer, 0, read)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "读取终端输出失败，会话 ID：{TerminalSessionId}", Id);
        }
    }

    private async Task WatchAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested && !process.HasExited())
                await Task.Delay(200, cancellation.Token);
            if (Volatile.Read(ref closed) == 0)
                await publish(new TerminalEvent("exited", Id, null, "终端已退出", process.ExitCode));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "终端进程监控失败，会话 ID：{TerminalSessionId}", Id);
        }
        finally
        {
            if (Volatile.Read(ref closed) == 0) await CloseAsync(fromWatchTask: true);
        }
    }

    public Task CloseAsync() => CloseAsync(fromWatchTask: false);

    private async Task CloseAsync(bool fromWatchTask)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            if (!fromWatchTask) await closeCompleted.Task;
            return;
        }
        cancellation.Cancel();
        try
        {
            input.Dispose();
            if (!process.HasExited())
            {
                process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "终止终端进程失败：{TerminalSessionId}", Id);
        }

        try
        {
            process.ClosePseudoConsoleHandle();
            await process.CloseOutputAsync();
            await AwaitCompletionAsync(outputTask);
            if (!fromWatchTask)
                await AwaitCompletionAsync(watchTask);
            await process.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "释放终端会话资源失败：{TerminalSessionId}", Id);
        }
        finally
        {
            cancellation.Dispose();
            try { await remove(this); }
            finally { closeCompleted.TrySetResult(); }
        }
    }

    private async Task AwaitCompletionAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "终端后台任务结束异常：{TerminalSessionId}", Id);
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
