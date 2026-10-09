namespace WinPilot.Features.Files;

public sealed record FileDownloadArtifact(Stream Content, string FileName, string ContentType);
