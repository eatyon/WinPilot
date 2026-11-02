namespace WinPilot.PluginContract;

public sealed record PluginFeatureActionResult(bool Succeeded, string Message)
{
    public static PluginFeatureActionResult Success(string message) => new(true, message);

    public static PluginFeatureActionResult Failure(string message) => new(false, message);
}

public delegate PluginFeatureActionResult PluginFeatureActionHandler();

public interface IPluginFeatureActionApi
{
    IDisposable Register(string featureId, PluginFeatureActionHandler handler);
}
