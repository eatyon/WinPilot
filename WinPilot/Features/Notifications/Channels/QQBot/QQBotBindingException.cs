namespace WinPilot.Features.Notifications;

internal sealed class QQBotBindingValidationException(string message) : InvalidOperationException(message);

internal sealed class QQBotBindingTimeoutException(Exception inner) : TimeoutException("获取超时，请重试", inner);
