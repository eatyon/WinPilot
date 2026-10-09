namespace WinPilot.Features.Terminal;

internal sealed class TaskShellOutputWaiter : IDisposable
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private readonly string shell;
    private readonly string beginMarker = $"__WINPILOT_BEGIN_{Guid.NewGuid():N}__";
    private readonly string endMarker = $"__WINPILOT_END_{Guid.NewGuid():N}__";
    private readonly TaskCompletionSource<bool> outputReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool atLineStart = true;
    private bool matchingBegin;
    private bool matchingEnd;
    private bool began;
    private bool ended;
    private bool skipDelimiter = true;
    private int beginIndex;
    private int endIndex;
    private int ansiState;
    private bool disposed;

    public string WrappedCommand { get; }

    public TaskShellOutputWaiter(string shell, string command)
    {
        this.shell = shell == "cmd" ? "CMD" : "PowerShell";
        WrappedCommand = shell == "cmd" ? $"echo {beginMarker}& {command} & echo {endMarker}\r\n" : $"Write-Output '{beginMarker}'; & {{ {command} }}; Write-Output '{endMarker}'\r\n";
    }

    public void Observe(TerminalEvent item)
    {
        if (item.Type != "output")
            return;
        lock (gate)
        {
            if (disposed || ended)
                return;
            foreach (var character in item.Data)
            {
                ConsumeCharacter(character);
                if (ended || outputReceived.Task.IsCompleted)
                    break;
            }
        }
    }

    private void ConsumeCharacter(char character)
    {
        if (ansiState == 1)
        {
            ansiState = character == '[' ? 2 : character == ']' ? 3 : 0;
            return;
        }

        if (ansiState == 2)
        {
            if (character is >= '@' and <= '~')
                ansiState = 0;
            return;
        }

        if (ansiState == 3)
        {
            if (character == '\a')
                ansiState = 0;
            else if (character == '\x1b')
                ansiState = 4;
            return;
        }

        if (ansiState == 4)
        {
            ansiState = character == '\\' ? 0 : 3;
            return;
        }

        if (character == '\x1b')
        {
            ansiState = 1;
            return;
        }

        if (!began)
            ConsumeBeforeBegin(character);
        else
            ConsumeCommandOutput(character);
    }

    public async Task<ShellCommandResult> WaitAsync(CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = waitCancellation.Token.Register(() => outputReceived.TrySetCanceled(waitCancellation.Token));
        var timeout = Task.Delay(MaximumWait, waitCancellation.Token);
        try
        {
            if (await Task.WhenAny(outputReceived.Task, timeout) == outputReceived.Task)
            {
                await outputReceived.Task;
                return new ShellCommandResult(shell, true);
            }

            await timeout;
            return new ShellCommandResult(shell, false);
        }
        finally
        {
            waitCancellation.Cancel();
            try
            {
                await timeout;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void ConsumeBeforeBegin(char character)
    {
        if (matchingBegin)
        {
            if (character == beginMarker[beginIndex])
            {
                beginIndex++;
                if (beginIndex == beginMarker.Length)
                {
                    began = true;
                    matchingBegin = false;
                    atLineStart = false;
                }

                return;
            }

            matchingBegin = false;
            beginIndex = 0;
        }

        if (atLineStart && character == beginMarker[0])
        {
            matchingBegin = true;
            beginIndex = 1;
            return;
        }

        UpdateLineStart(character);
    }

    private void ConsumeCommandOutput(char character)
    {
        if (matchingEnd)
        {
            if (character == endMarker[endIndex])
            {
                endIndex++;
                if (endIndex == endMarker.Length)
                    ended = true;
                return;
            }

            matchingEnd = false;
            endIndex = 0;
            outputReceived.TrySetResult(true);
            return;
        }

        if (skipDelimiter && (character == '\r' || character == '\n'))
        {
            atLineStart = true;
            return;
        }

        skipDelimiter = false;
        if (atLineStart && character == endMarker[0])
        {
            matchingEnd = true;
            endIndex = 1;
            return;
        }

        outputReceived.TrySetResult(true);
        UpdateLineStart(character);
    }

    private void UpdateLineStart(char character) => atLineStart = character is '\r' or '\n';

    public void Dispose()
    {
        lock (gate)
            disposed = true;
    }
}
