namespace WinPilot.Features.Plugins;

internal sealed class PluginOperationException(string message, Exception innerException, bool logged = false) : Exception(message, innerException)
{
    internal bool Logged { get; } = logged;
}
