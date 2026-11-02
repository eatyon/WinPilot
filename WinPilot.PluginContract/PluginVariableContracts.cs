namespace WinPilot.PluginContract;

public enum PluginVariableType
{
    Text,
    Image
}

public sealed record PluginVariableDefinition(
    string Name,
    string DisplayName,
    string Description = "",
    PluginVariableType Type = PluginVariableType.Text);

/// <summary>
/// 插件提供的图片字节和 MIME 类型；通知入口支持的格式与大小由宿主校验。
/// </summary>
public sealed record PluginImageContent(byte[] Content, string ContentType);

public delegate Task<IReadOnlyDictionary<string, object?>> PluginNotificationVariableResolver(
    IReadOnlyList<string> names,
    PluginTaskRunContext context,
    CancellationToken cancellationToken);

/// <summary>
/// 注册通知变量及可选解析器，解析器按发送时实际需要的变量名称提供值。
/// </summary>
public interface IPluginNotificationVariableApi
{
    IDisposable Register(IReadOnlyList<PluginVariableDefinition> definitions);

    IDisposable Register(
        IReadOnlyList<PluginVariableDefinition> definitions,
        PluginNotificationVariableResolver resolver);
}
