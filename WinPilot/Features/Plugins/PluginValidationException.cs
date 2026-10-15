namespace WinPilot.Features.Plugins;

internal sealed class PluginValidationException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);
