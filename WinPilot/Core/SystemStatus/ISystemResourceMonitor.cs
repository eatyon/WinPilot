namespace WinPilot.Core.SystemStatus;

public interface ISystemResourceMonitor
{
    SystemResourceStatus Capture(CancellationToken cancellationToken);
}
