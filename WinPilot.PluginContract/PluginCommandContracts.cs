namespace WinPilot.PluginContract;

public sealed record PluginCommandDefinition(
    string Name,
    string Description,
    string? Parameters = null,
    bool AllowAttachedArguments = false)
{
    public string Usage => Parameters is null ? Name : $"{Name} {Parameters}";
}

public sealed record PluginCommandContext(string Name, string Arguments, IPluginCommandReply Reply);

public delegate Task PluginCommandHandler(PluginCommandContext context, CancellationToken cancellationToken);

/// <summary>
/// 向宿主命令机器人注册插件命令，释放返回的注册对象即可移除命令。
/// </summary>
public interface IPluginCommandApi
{
    IDisposable Register(PluginCommandDefinition definition, PluginCommandHandler handler);
}

/// <summary>
/// 当前命令的回复入口，由宿主适配来源渠道，插件无需直接管理渠道凭据。
/// </summary>
public interface IPluginCommandReply
{
    Task ReplyTextAsync(string text);

    Task ReplyImageAsync(PluginImageContent image);
}
