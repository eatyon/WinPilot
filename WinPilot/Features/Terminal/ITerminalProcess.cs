namespace WinPilot.Features.Terminal;

internal interface ITerminalProcess : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    int? ExitCode { get; }
    bool HasExited();
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Resize(short columns, short rows);
    void ClosePseudoConsoleHandle();
    ValueTask CloseOutputAsync();
}
