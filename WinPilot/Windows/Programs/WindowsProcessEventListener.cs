using System.Management;

namespace WinPilot.Windows;

public enum WindowsProcessEventType { Started, Exited }

public sealed record WindowsProcessEvent(WindowsProcessEventType Type, int ProcessId, string ProcessName);

public sealed class WindowsProcessEventListener(ILogger<WindowsProcessEventListener> logger) : IDisposable
{
    private readonly object gate = new();
    private ManagementEventWatcher? startedWatcher;
    private ManagementEventWatcher? exitedWatcher;

    public event EventHandler<WindowsProcessEvent>? Changed;

    public bool Start()
    {
        lock (gate)
        {
            if (startedWatcher is not null) return true;
            try
            {
                startedWatcher = CreateWatcher("Win32_ProcessStartTrace", WindowsProcessEventType.Started);
                exitedWatcher = CreateWatcher("Win32_ProcessStopTrace", WindowsProcessEventType.Exited);
                startedWatcher.Start();
                exitedWatcher.Start();
                return true;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "无法启动 Windows 进程事件监听器");
                StopCore();
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (gate) StopCore();
    }

    public void Dispose()
    {
        Stop();
    }

    private ManagementEventWatcher CreateWatcher(string traceClass, WindowsProcessEventType type)
    {
        var watcher = new ManagementEventWatcher(@"root\CIMV2", $"SELECT * FROM {traceClass}");
        watcher.EventArrived += (_, args) => HandleEvent(type, args);
        return watcher;
    }

    private void HandleEvent(WindowsProcessEventType type, EventArrivedEventArgs args)
    {
        try
        {
            var processId = Convert.ToInt32(args.NewEvent["ProcessID"]);
            var rawProcessName = Convert.ToString(args.NewEvent["ProcessName"]) ?? "";
            var processName = Path.GetFileNameWithoutExtension(rawProcessName);
            if (processId <= 0 || string.IsNullOrWhiteSpace(processName))
            {
                logger.LogDebug("已忽略无效进程事件，事件类型：{Type}，进程 ID：{ProcessId}，原因：进程 ID 或名称无效", type, processId);
                return;
            }
            Changed?.Invoke(this, new WindowsProcessEvent(type, processId, processName));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "无法处理 Windows 进程事件");
        }
    }

    private void StopCore()
    {
        DisposeWatcher(ref startedWatcher);
        DisposeWatcher(ref exitedWatcher);
    }

    private static void DisposeWatcher(ref ManagementEventWatcher? watcher)
    {
        if (watcher is null) return;
        try { watcher.Stop(); } catch { }
        watcher.Dispose();
        watcher = null;
    }
}
