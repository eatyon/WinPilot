namespace WinPilot.Core;

internal sealed class ResourceNameConflictException(string message) : InvalidOperationException(message);
