namespace WinPilot.Core.Notifications;

public sealed record NotificationMessage(string Title, string Content, string? ImagePath = null, string Event = "WinPilot", string Result = "Success", string? Timestamp = null, object? Data = null);
public sealed record NotificationChannelResult(string Channel, bool Succeeded, string? Error = null);
