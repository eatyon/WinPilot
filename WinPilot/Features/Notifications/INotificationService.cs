using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

public interface INotificationService
{
    Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default, string? channel = null, bool ignoreChannelEnabled = false);
}
