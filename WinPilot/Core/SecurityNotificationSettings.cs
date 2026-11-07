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
    SecurityNotificationSettingsChanged,
    SessionInvalidated
}

public sealed record SecurityNotificationSettings
{
    public string Channel { get; init; } = "";
    public IReadOnlyList<SecurityEventType> Events { get; init; } = SecurityEventDefaults.Notified;
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
