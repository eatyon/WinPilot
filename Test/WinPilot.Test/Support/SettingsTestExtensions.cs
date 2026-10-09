using WinPilot.Core;
namespace WinPilot.Test;

internal static class SettingsTestExtensions
{
    internal static void Save(this WinPilotSettings settings, string configurationFile) =>
        WinPilotSettings.Update(configurationFile, _ => settings);
}
