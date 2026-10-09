namespace WinPilot.PluginContract;

public sealed record PluginCommandDefinition(string Name, string Description, string? Parameters = null, bool AllowAttachedArguments = false)
{
    public string Usage => Parameters is null ? Name : $"{Name} {Parameters}";
}
public sealed record PluginCommandContext(string Name, string Arguments, IPluginCommandReply Reply);
public delegate Task PluginCommandHandler(PluginCommandContext context, CancellationToken cancellationToken);

public interface IPluginCommandApi
{
    IDisposable Register(PluginCommandDefinition definition, PluginCommandHandler handler);
}

public interface IPluginCommandReply
{
    Task ReplyTextAsync(string text);
    Task ReplyImageAsync(PluginImageContent image);
}
