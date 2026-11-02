namespace WinPilot.Features.Plugins;

internal sealed class RegistryRegistration(Action dispose) : IDisposable
{
    private Action? action = dispose;

    public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
}
