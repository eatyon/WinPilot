using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Features.Tasks;

public sealed record TaskVariableDescriptor(
    string Name,
    string DisplayName,
    string Description,
    PluginVariableType Type,
    string PluginId,
    string PluginDisplayName);

public sealed class TaskVariableCatalog(PluginTaskExtensionRegistry extensions)
{
    private static readonly IReadOnlyList<TaskVariableDescriptor> BuiltIn =
    [
        new("$time", "发送时间", "通知发送时的时间，格式为 [yyyy-MM-dd HH:mm:ss]", PluginVariableType.Text, "winpilot", "WinPilot"),
        new("&screenshot", "桌面截图", "发送当前电脑桌面截图", PluginVariableType.Image, "winpilot", "WinPilot"),
        new("&image=\"\"", "本地图片", "发送指定的本地 PNG/JPEG 图片，格式为 &image=\"图片绝对路径\"", PluginVariableType.Image, "winpilot", "WinPilot")
    ];

    public IReadOnlyList<TaskVariableDescriptor> GetAll() =>
    [
        .. BuiltIn,
        .. extensions.GetVariables()
            .Where(variable => variable.Available)
            .Select(variable => new TaskVariableDescriptor(
                variable.Name,
                variable.DisplayName,
                variable.Description,
                variable.Type,
                variable.PluginId,
                variable.PluginDisplayName))
    ];

    public bool IsRegisteredImage(string name) => MatchRegisteredImage(name) is not null;

    public string? MatchRegisteredImage(string token) => GetAll()
        .Where(variable => variable.Type == PluginVariableType.Image && variable.Name != "&image=\"\"")
        .Select(variable => variable.Name)
        .FirstOrDefault(name => token.Equals(name, StringComparison.OrdinalIgnoreCase));
}
