namespace WinPilot.PluginContract;

public enum PluginHostActionType
{
    StartProgram,
    Delay,
    Keyboard,
    Shell,
    SendNotification
}

public sealed record PluginHostActionStep(PluginHostActionType Type, IReadOnlyDictionary<string, object?> Parameters);

public sealed record PluginHostActionPlan(IReadOnlyList<PluginHostActionStep> Steps);

public sealed record PluginHostActionResult(bool Succeeded, string? Error = null);

public interface IPluginHostActionApi
{
    Task<PluginHostActionResult> ExecuteAsync(PluginHostActionStep step, CancellationToken cancellationToken = default);
}

public interface IPluginProcessApi
{
    bool IsRunning(string processName);
}

public interface IPluginHostSecurityApi
{
    bool SafeModeEnabled { get; }
}
