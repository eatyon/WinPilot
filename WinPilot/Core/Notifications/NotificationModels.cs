namespace WinPilot.Core.Notifications;

/// <summary>
/// 渠道共用的通知内容；正文不单设标题，图片使用本地路径，事件等元数据由渠道按需适配。
/// </summary>
public sealed record NotificationMessage(
    string Content,
    string? ImagePath = null,
    string Event = "WinPilot",
    string Result = "Success",
    string? Timestamp = null,
    object? Data = null);

/// <summary>
/// 渠道投递结果；Succeeded 表示渠道发送调用成功，不代表接收方已阅读。
/// </summary>
public sealed record NotificationChannelResult(string Channel, bool Succeeded, string? Error = null);
