namespace WinPilot.Core.Desktop;

public sealed record DesktopScreenshot(DateTimeOffset CapturedAt, byte[] Content, string ContentType);
