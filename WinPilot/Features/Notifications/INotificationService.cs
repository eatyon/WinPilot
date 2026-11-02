using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

/// <summary>
/// 通知渠道发送入口；返回渠道结果，调用方需检查失败项，主动取消通过异常传播。
/// </summary>
public interface INotificationService
{
    Task<IReadOnlyList<NotificationChannelResult>> SendAsync(
        NotificationMessage message,
        string? channel,
        CancellationToken cancellationToken = default);
}
