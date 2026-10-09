namespace WinPilot.Core.Desktop;

public sealed class DesktopCaptureException(string message, Exception? innerException = null) : Exception(message, innerException);
