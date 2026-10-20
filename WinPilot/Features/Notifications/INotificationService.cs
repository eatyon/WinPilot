using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

public interface INotificationService
{
    Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, string? channel, CancellationToken cancellationToken = default);
}
