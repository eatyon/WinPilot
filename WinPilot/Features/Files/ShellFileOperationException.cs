namespace WinPilot.Features.Files;

internal sealed class ShellFileOperationException(string message, int returnCode, bool anyOperationsAborted) : IOException(message)
{
    public int ReturnCode { get; } = returnCode;
    public bool AnyOperationsAborted { get; } = anyOperationsAborted;
}
