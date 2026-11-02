using System.Diagnostics;
using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed class PluginProcessApi : IPluginProcessApi
{
    public bool IsRunning(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
