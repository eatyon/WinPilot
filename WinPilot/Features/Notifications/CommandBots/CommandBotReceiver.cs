using WinPilot.Core;

namespace WinPilot.Features.Notifications.CommandBots;

internal abstract class CommandBotReceiver(Func<CommandBotStatus> status,
    Action<CommandBotStatus> publish, Action<CommandBotMessage> receive)
{
    protected CommandBotStatus Status => status();
    protected void SetStatus(CommandBotStatus value) => publish(value);
    protected void Receive(CommandBotMessage message) => receive(message);
    public abstract Task RunAsync(WinPilotSettings settings, CancellationToken token);
}
