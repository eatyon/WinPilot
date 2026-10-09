using WinPilot.Core;

namespace WinPilot.Web;

public static class WebAccessPolicy
{
    public static bool CanListenOnLan(WinPilotSettings settings) => settings.LanAccessEnabled;
}
