using System.Text;

namespace WinPilot.Features.Terminal;

internal sealed class TerminalSession : IAsyncDisposable
{
    private readonly ITerminalProcess process;
    private readonly Func<TerminalEvent, Task> publish;
    private readonly Func<TerminalSession, Task> remove;
    private readonly ILogger logger;
    private readonly CancellationTokenSource cancellation;
    private readonly StreamWriter input;
    private Task? outputTask;
    private Task? outputCloseTask;
    private Task? watchTask;
    private int outputReadFailed;
    private int closed;
    private readonly TaskCompletionSource closeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid Id { get; }

    public Guid ConnectionId { get; }

    internal TerminalSession(Guid id, Guid connectionId, ITerminalProcess process, Func<TerminalEvent, Task> publish, Func<TerminalSession, Task> remove, ILogger logger, CancellationTokenSource? cancellation = null)
    {
        Id = id;
        ConnectionId = connectionId;
        this.process = process;
        this.publish = publish;
        this.remove = remove;
        this.logger = logger;
        this.cancellation = cancellation ?? new();
        input = new StreamWriter(process.Input, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public void Start()
    {
        outputTask = ReadAsync(process.Output, "stdout");
        watchTask = WatchAsync();
        outputCloseTask = CloseAfterReadFailureAsync(outputTask);
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
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var buffer = new char[4096];
            while (!cancellation.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token);
                if (read == 0)
                    break;
                await publish(new TerminalEvent("output", Id, kind, new string (buffer, 0, read)));
            }
        }
        catch (Exception) when (Volatile.Read(ref closed) != 0 || cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(ref outputReadFailed, 1);
            logger.LogWarning(exception, "读取终端输出失败，会话 ID：{TerminalSessionId}", Id);
        }
    }

    private async Task CloseAfterReadFailureAsync(Task reading)
    {
        await reading;
        if (Volatile.Read(ref outputReadFailed) != 0) await CloseAsync(fromWatchTask: false);
    }

    private async Task WatchAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested && !process.HasExited())
                await Task.Delay(200, cancellation.Token);
            if (Volatile.Read(ref closed) == 0 && Volatile.Read(ref outputReadFailed) == 0)
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

    public async Task CloseAsync()
    {
        await CloseAsync(fromWatchTask: false);
        await AwaitCompletionAsync(outputCloseTask, "等待输出故障收尾");
    }

    private async Task CloseAsync(bool fromWatchTask)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            if (!fromWatchTask) await closeCompleted.Task;
            return;
        }
        var failedOutput = Volatile.Read(ref outputReadFailed) != 0;
        try
        {
            if (failedOutput)
                await CloseStageAsync("发送输出故障通知", () => new ValueTask(publish(new TerminalEvent("error", Id, null, "终端输出读取失败，正在关闭会话"))));
            CloseStage("取消会话操作", cancellation.Cancel);
            CloseStage("关闭输入", input.Dispose);
            var waitForExit = false;
            CloseStage("终止进程", () =>
            {
                if (process.HasExited()) return;
                process.Kill();
                waitForExit = true;
            });
            if (waitForExit)
                await CloseStageAsync("等待进程退出", () => new ValueTask(process.WaitForExitAsync(CancellationToken.None)));
            if (!failedOutput) CloseStage("关闭伪控制台", process.ClosePseudoConsoleHandle);
            var outputClosed = await CloseStageAsync("关闭输出", process.CloseOutputAsync);
            if (!outputClosed)
                await CloseStageAsync("释放底层资源", process.DisposeAsync);
            if (failedOutput) CloseStage("关闭伪控制台", process.ClosePseudoConsoleHandle);
            await AwaitCompletionAsync(outputTask, "等待输出任务");
            if (!fromWatchTask)
                await AwaitCompletionAsync(watchTask, "等待监控任务");
            if (outputClosed)
                await CloseStageAsync("释放底层资源", process.DisposeAsync);
        }
        finally
        {
            try
            {
                CloseStage("释放取消源", cancellation.Dispose);
                await CloseStageAsync("移除会话", () => new ValueTask(remove(this)));
                if (failedOutput)
                    await CloseStageAsync("发送会话结束通知", () => new ValueTask(publish(new TerminalEvent("exited", Id, null, "终端会话已结束"))));
            }
            finally { closeCompleted.TrySetResult(); }
        }
    }

    private void CloseStage(string stage, Action close)
    {
        try { close(); }
        catch (Exception exception) { logger.LogWarning(exception, "终端会话收尾失败，阶段：{Stage}，会话 ID：{TerminalSessionId}", stage, Id); }
    }

    private async Task<bool> CloseStageAsync(string stage, Func<ValueTask> close)
    {
        try { await close(); return true; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "终端会话收尾失败，阶段：{Stage}，会话 ID：{TerminalSessionId}", stage, Id);
            return false;
        }
    }

    private async Task AwaitCompletionAsync(Task? task, string stage)
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
            logger.LogWarning(exception, "终端会话收尾失败，阶段：{Stage}，会话 ID：{TerminalSessionId}", stage, Id);
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
