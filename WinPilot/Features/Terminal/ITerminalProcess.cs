namespace WinPilot.Features.Terminal;

/// <summary>
/// 终端会话使用的底层进程契约，将输入输出、进程退出与 ConPTY 资源关闭分开处理。
/// </summary>
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
