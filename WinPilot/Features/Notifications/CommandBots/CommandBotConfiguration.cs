using WinPilot.Core;

namespace WinPilot.Features.Notifications.CommandBots;

public sealed class CommandBotConfiguration : IDisposable
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly List<CommandBotService> bots = [];
    internal void Register(CommandBotService bot) => bots.Add(bot);
    internal void Unregister(CommandBotService bot) => bots.Remove(bot);
    internal async Task<NotificationSettings> SaveNotificationsAsync(string configurationFile,
        Func<NotificationSettings, NotificationSettings> update, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            CommandBotService[] changed = [];
            var saved = WinPilotSettings.Update(configurationFile, current =>
            {
                var previous = current.Notifications;
                var next = update(previous);
                changed = bots.Where(bot => bot.ConnectionChanged(previous, next, current.CommandBots)).ToArray();
                foreach (var bot in changed) bot.ValidateConnection(next);
                return current with { Notifications = next };
            });
            foreach (var bot in changed) await bot.RefreshAsync();
            return saved.Notifications;
        }
        finally { Gate.Release(); }
    }
    public void Dispose() => Gate.Dispose();
}
