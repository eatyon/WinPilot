namespace WinPilot.Features.Files;

internal sealed class FileEditConflictException(string message) : InvalidOperationException(message);
