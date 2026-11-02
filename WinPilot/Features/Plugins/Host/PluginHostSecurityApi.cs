using WinPilot.Features.Security;
using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed class PluginHostSecurityApi(SafeModeService safeMode) : IPluginHostSecurityApi
{
    public bool SafeModeEnabled => safeMode.Enabled;
}
