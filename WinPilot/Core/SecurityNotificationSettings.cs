namespace WinPilot.Core;

public enum SecurityEventType
{
    PasswordSet,
    PasswordChanged,
    LoginSucceeded,
    LoginFailed,
    LoginSourceLocked,
    AuthConfigurationInvalid,
    LanAccessChanged,
    SessionsRevoked,
    SecurityNotificationSettingsChanged
}

public enum SecurityNotificationDeliveryMode
{
    AllEnabled,
    SpecificChannels
}

public sealed record SecurityNotificationSettings
{
    public bool Enabled { get; init; }
    public IReadOnlyList<SecurityEventType> Events { get; init; } = SecurityEventDefaults.Notified;
    public SecurityNotificationDeliveryMode DeliveryMode { get; init; } = SecurityNotificationDeliveryMode.AllEnabled;
    public IReadOnlyList<string> Channels { get; init; } = [];
}

public static class SecurityEventDefaults
{
    public static readonly IReadOnlyList<SecurityEventType> Notified = [
        SecurityEventType.PasswordSet,
        SecurityEventType.PasswordChanged,
        SecurityEventType.LanAccessChanged,
        SecurityEventType.AuthConfigurationInvalid,
        SecurityEventType.LoginSourceLocked
    ];
}
