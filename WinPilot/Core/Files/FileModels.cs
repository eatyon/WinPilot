using System.Text.Json.Serialization;

namespace WinPilot.Core.Files;

public sealed record FileSystemRoot(string Path, string Name, string Kind);

public sealed record FileSystemEntry(
    string Name,
    string Path,
    bool IsDirectory,
    bool IsReparsePoint,
    bool IsSensitiveSystemPath,
    string Type,
    long? Size,
    DateTimeOffset LastWriteTime);

public sealed record FileDirectoryResult(
    string Path,
    string? ParentPath,
    IReadOnlyList<FileSystemEntry> Entries,
    bool IsSensitiveSystemPath);

public sealed record CreateFileSystemItemRequest(string ParentPath, string Name);

public sealed record RenameFileSystemItemRequest(string Path, string Name);

public sealed record DeleteFileSystemItemsRequest(IReadOnlyList<string> Paths);

public sealed record TransferFileSystemItemsRequest(IReadOnlyList<string> SourcePaths, string DestinationDirectory);

public sealed record DownloadFileSystemItemsRequest(IReadOnlyList<string> Paths);

public sealed record EditFileSystemItemRequest(
    string Path,
    string Content,
    long OriginalLength,
    DateTimeOffset OriginalLastWriteTime,
    string Encoding);

public sealed record EditFileSystemItemResult(
    string Path,
    string Content,
    long Length,
    DateTimeOffset LastWriteTime,
    string Encoding);

public sealed record BeginFileUploadRequest(string Directory, string FileName, [property: JsonRequired] long Size);

public sealed record FileUploadChunkRequest(string UploadId, long Offset);

public sealed record CompleteFileUploadRequest(string UploadId);

public sealed record FileUploadStartResult(string UploadId);
