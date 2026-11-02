namespace WinPilot.Features.Files;

/// <summary>
/// 下载响应所需的内容与元信息；Content 的所有权交给响应调用方，使用结束后必须释放。
/// </summary>
public sealed record FileDownloadArtifact(Stream Content, string FileName, string ContentType);
