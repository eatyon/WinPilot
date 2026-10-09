using System.Reflection;

namespace WinPilot.Core;

public static class WinPilotVersion
{
    private static readonly Assembly Assembly = typeof(WinPilotVersion).Assembly;

    public static string DisplayVersion { get; } = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static Version Version { get; } = Assembly.GetName().Version ?? new Version(0, 0, 0);
}
