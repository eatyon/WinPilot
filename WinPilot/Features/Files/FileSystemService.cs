using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices;
using WinPilot.Core;
using WinPilot.Core.Files;

namespace WinPilot.Features.Files;

public sealed class FileSystemService(ILogger<FileSystemService> logger)
{
    private static readonly object[] EditGates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public IReadOnlyList<FileSystemRoot> GetRoots()
    {
        var roots = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new FileSystemRoot(drive.RootDirectory.FullName, BuildDriveName(drive), "驱动器"))
            .OrderBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roots;
    }

    public FileDirectoryResult GetDirectory(string path)
    {
        var directory = RequireDirectory(path);
        var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos()
            .Select(ToEntry)
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new FileDirectoryResult(directory, GetParent(directory), entries, IsSensitiveSystemPath(directory));
    }

    public async Task UploadAsync(string directoryPath, IFormFile file, CancellationToken cancellationToken)
    {
        var directory = RequireDirectory(directoryPath);
        var fileName = Path.GetFileName(file.FileName.Replace('/', '\\'));
        ValidateName(fileName);
        var target = Path.Combine(directory, fileName);
        RejectExistingDestination(target);
        var temporaryPath = Path.Combine(directory, $".winpilot-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            RejectExistingDestination(target);
            File.Move(temporaryPath, target);
            logger.LogInformation("文件操作：上传文件 {Path}", target);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public void CreateDirectory(CreateFileSystemItemRequest request)
    {
        var target = CombineNewItem(request.ParentPath, request.Name);
        Directory.CreateDirectory(target);
        logger.LogInformation("文件操作：新建文件夹 {Path}", target);
    }

    public void CreateFile(CreateFileSystemItemRequest request)
    {
        var target = CombineNewItem(request.ParentPath, request.Name);
        using (new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        logger.LogInformation("文件操作：新建文件 {Path}", target);
    }

    public void Rename(RenameFileSystemItemRequest request)
    {
        var source = RequireExisting(request.Path);
        RejectRoot(source);
        ValidateName(request.Name);
        var destination = Path.Combine(Path.GetDirectoryName(source)!, request.Name);
        RejectExistingDestination(destination);
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
        logger.LogInformation("文件操作：重命名 {Source} 为 {Destination}", source, destination);
    }

    public void Delete(DeleteFileSystemItemsRequest request)
    {
        var paths = RequireDistinctPaths(request.Paths);
        foreach (var path in paths)
        {
            RejectRoot(path);
        }
        ExecuteShellOperation(FoDelete, paths, null, FofAllowUndo, "移入回收站");
        logger.LogInformation("文件操作：已移入回收站，共 {Count} 项：{Paths}", paths.Count, string.Join(" | ", paths));
    }

    public void Copy(TransferFileSystemItemsRequest request)
    {
        var sources = RequireDistinctPaths(request.SourcePaths);
        var destinationDirectory = RequireDirectory(request.DestinationDirectory);
        ValidateTransfer(sources, destinationDirectory);
        ExecuteShellOperation(FoCopy, sources, destinationDirectory, 0, "复制");
        logger.LogInformation("文件操作：复制 {Count} 项到 {DestinationDirectory}：{Paths}", sources.Count, destinationDirectory, string.Join(" | ", sources));
    }

    public void Move(TransferFileSystemItemsRequest request)
    {
        var sources = RequireDistinctPaths(request.SourcePaths);
        var destinationDirectory = RequireDirectory(request.DestinationDirectory);
        ValidateTransfer(sources, destinationDirectory);
        ExecuteShellOperation(FoMove, sources, destinationDirectory, 0, "移动");
        logger.LogInformation("文件操作：移动 {Count} 项到 {DestinationDirectory}：{Paths}", sources.Count, destinationDirectory, string.Join(" | ", sources));
    }

    public EditFileSystemItemResult ReadForEdit(string path)
    {
        var fullPath = RequireExisting(path);
        if (Directory.Exists(fullPath)) throw new InvalidOperationException("此项目不支持编辑");
        var info = new FileInfo(fullPath);
        if (info.Length > MaxEditableFileSize) throw new InvalidOperationException("文件过大，不支持在此处编辑");
        using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxEditableFileSize) throw new InvalidOperationException("文件过大，不支持在此处编辑");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        info.Refresh();
        var (encoding, preambleLength) = DetectTextEncoding(bytes);
        string content;
        try { content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("此文件不支持编辑"); }
        return new EditFileSystemItemResult(fullPath, content, info.Length, info.LastWriteTimeUtc, EncodingName(encoding));
    }

    public void SaveEdit(EditFileSystemItemRequest request)
    {
        var path = RequireExisting(request.Path);
        var editGate = EditGates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % (uint)EditGates.Length];
        lock (editGate) SaveEditCore(request, path);
    }

    private void SaveEditCore(EditFileSystemItemRequest request, string path)
    {
        if (Directory.Exists(path)) throw new InvalidOperationException("此项目不支持编辑");
        var info = new FileInfo(path);
        if (info.Length != request.OriginalLength || info.LastWriteTimeUtc != request.OriginalLastWriteTime) throw new FileEditConflictException("文件在编辑期间已被其他程序修改");
        var encoding = ParseTextEncoding(request.Encoding);
        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[3];
            var length = input.Read(header);
            if (header[..length].StartsWith(Encoding.UTF8.GetPreamble())) encoding = new UTF8Encoding(true, true);
        }
        if (encoding.GetByteCount(request.Content) + encoding.GetPreamble().Length > MaxEditableFileSize)
            throw new InvalidOperationException("文件过大，不支持在此处编辑");
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".winpilot-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            var preamble = encoding.GetPreamble();
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(preamble);
                using var writer = new StreamWriter(output, encoding, leaveOpen: true);
                writer.Write(request.Content);
                writer.Flush();
                output.Flush(true);
            }
            info.Refresh();
            if (info.Length != request.OriginalLength || info.LastWriteTimeUtc != request.OriginalLastWriteTime)
                throw new FileEditConflictException("文件在编辑期间已被其他程序修改");
            File.Replace(temporaryPath, path, null);
            logger.LogInformation("文件操作：编辑保存 {Path}", path);
        }
        finally { TryDelete(temporaryPath); }
    }

    private const long MaxEditableFileSize = 10 * 1024 * 1024;

    private static (Encoding Encoding, int PreambleLength) DetectTextEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) return (new UTF8Encoding(true, true), Encoding.UTF8.GetPreamble().Length);
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())) return (Encoding.Unicode, Encoding.Unicode.GetPreamble().Length);
        if (bytes.AsSpan().IndexOf((byte)0) >= 0) throw new InvalidOperationException("此文件不支持编辑");
        return (new UTF8Encoding(false, true), 0);
    }

    private static string EncodingName(Encoding encoding) => encoding.CodePage == Encoding.Unicode.CodePage ? "utf-16" : "utf-8";

    private static Encoding ParseTextEncoding(string? value) => string.Equals(value, "utf-16", StringComparison.OrdinalIgnoreCase) ? Encoding.Unicode : string.Equals(value, "utf-8", StringComparison.OrdinalIgnoreCase) ? new UTF8Encoding(false, true) : throw new InvalidOperationException("此文件不支持编辑");

    public Task<FileDownloadArtifact> CreateDownloadAsync(DownloadFileSystemItemsRequest request, CancellationToken cancellationToken)
        => CreateDownloadAsync(request, cancellationToken, Path.GetTempPath());

    internal async Task<FileDownloadArtifact> CreateDownloadAsync(DownloadFileSystemItemsRequest request, CancellationToken cancellationToken, string temporaryDirectory)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = RequireDistinctPaths(request.Paths);
        if (paths.Count == 1 && File.Exists(paths[0]))
        {
            var path = paths[0];
            return new FileDownloadArtifact(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), Path.GetFileName(path), "application/octet-stream");
        }

        var temporaryPath = Path.Combine(temporaryDirectory, $"WinPilot-download-{Guid.NewGuid():N}.zip");
        try
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var archive = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken: cancellationToken))
            {
                foreach (var path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    if (string.IsNullOrWhiteSpace(rootName)) throw new InvalidOperationException("无法下载驱动器根目录");
                    if (File.Exists(path)) await AddFileToArchiveAsync(archive, path, rootName, usedNames, cancellationToken);
                    else await AddArchiveDirectoryAsync(archive, path, rootName, usedNames, cancellationToken);
                }
            }
            var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            return new FileDownloadArtifact(stream, paths.Count == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0])) + ".zip" : "Download.zip", "application/zip");
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task AddArchiveDirectoryAsync(ZipArchive archive, string directory, string relativePath, HashSet<string> usedNames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("文件夹包含目录链接，无法安全打包下载");
        AddArchiveEntryName(usedNames, relativePath + "/");
        archive.CreateEntry(relativePath + "/");
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childPath = relativePath + "/" + item.Name;
            if (item is DirectoryInfo childDirectory) await AddArchiveDirectoryAsync(archive, childDirectory.FullName, childPath, usedNames, cancellationToken);
            else await AddFileToArchiveAsync(archive, item.FullName, childPath, usedNames, cancellationToken);
        }
    }

    private static async Task AddFileToArchiveAsync(ZipArchive archive, string source, string relativePath, HashSet<string> usedNames, CancellationToken cancellationToken)
    {
        AddArchiveEntryName(usedNames, relativePath);
        var entry = archive.CreateEntry(relativePath, CompressionLevel.Fastest);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var entryStream = await entry.OpenAsync(cancellationToken);
        await input.CopyToAsync(entryStream, 128 * 1024, cancellationToken);
    }

    private static void AddArchiveEntryName(HashSet<string> usedNames, string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.Contains("../", StringComparison.Ordinal) || !usedNames.Add(normalized.TrimEnd('/'))) throw new InvalidOperationException("下载内容存在路径冲突");
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) { logger.LogWarning(exception, "无法清理文件操作临时文件 {Path}", path); }
    }

    private static string BuildDriveName(DriveInfo drive)
    {
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel;
        return $"{drive.Name} ({label})";
    }

    private static FileSystemEntry ToEntry(FileSystemInfo info)
    {
        var attributes = info.Attributes;
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        var type = isDirectory ? "目录" : "文件";
        return new FileSystemEntry(info.Name, info.FullName, isDirectory, attributes.HasFlag(FileAttributes.ReparsePoint), IsSensitiveSystemPath(info.FullName), type, isDirectory ? null : ((FileInfo)info).Length, info.LastWriteTimeUtc);
    }

    private static string? GetParent(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return null;
        return Directory.GetParent(path)?.FullName;
    }

    private static string CombineNewItem(string parentPath, string name)
    {
        var parent = RequireDirectory(parentPath);
        ValidateName(name);
        var target = Path.Combine(parent, name);
        RejectExistingDestination(target);
        return target;
    }

    private static IReadOnlyList<string> RequireDistinctPaths(IReadOnlyList<string>? input)
    {
        if (input is null || input.Count == 0) throw new InvalidOperationException("请至少选择一项");
        var paths = input.Select(RequireExisting).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length != input.Count) throw new InvalidOperationException("操作路径不能重复");
        return paths;
    }

    private static void ValidateTransfer(IReadOnlyList<string> sources, string destinationDirectory)
    {
        foreach (var source in sources)
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            RejectExistingDestination(destination);
            if (Directory.Exists(source)) RejectNestedDestination(source, destinationDirectory);
        }
    }

    private static void RejectNestedDestination(string source, string destination) { if (IsSameOrChildPath(source, destination)) throw new InvalidOperationException("不能复制或移动文件夹到自身或其子目录"); }
    private static bool IsSameOrChildPath(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string RequireDirectory(string? path)
    {
        var fullPath = RequireExisting(path);
        if (!Directory.Exists(fullPath)) throw new InvalidOperationException("路径不是文件夹");
        return fullPath;
    }

    private static string RequireExisting(string? path)
    {
        var fullPath = NormalizeAbsolutePath(path);
        _ = File.GetAttributes(fullPath);
        return fullPath;
    }

    private static string NormalizeAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidOperationException("请输入有效的 Windows 绝对路径");
        return Path.GetFullPath(path);
    }

    private static void RejectExistingDestination(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new ResourceNameConflictException($"目标已存在：{Path.GetFileName(path)}");
    }

    private static void RejectRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能对驱动器根目录执行此操作");
    }

    private static bool IsSensitiveSystemPath(string path)
    {
        var sensitiveRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        return sensitiveRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Any(root => IsSameOrChildPath(Path.GetFullPath(root), path));
    }

    private static void ExecuteShellOperation(uint operationType, IReadOnlyList<string> sources, string? destinationDirectory, ushort additionalFlags, string operationName)
    {
        var nativeSources = string.Concat(sources.Select(path => path + '\0')) + '\0';
        var operation = new ShellFileOperation
        {
            wFunc = operationType,
            pFrom = nativeSources,
            pTo = destinationDirectory is null ? null : destinationDirectory + '\0',
            fFlags = (ushort)(additionalFlags | FofNoConfirmation | FofSilent | FofNoErrorUi)
        };
        var errorCode = SHFileOperation(ref operation);
        if (errorCode != 0) throw new ShellFileOperationException($"无法{operationName}：{new Win32Exception(errorCode).Message}", errorCode, operation.fAnyOperationsAborted);
        if (operation.fAnyOperationsAborted) throw new ShellFileOperationException($"{operationName}操作已取消", errorCode, operation.fAnyOperationsAborted);
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") throw new InvalidOperationException("名称不能为空，且不能使用 . 或 ..");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) throw new InvalidOperationException("名称包含 Windows 文件名不支持的字符");
        if (ReservedFileNames.Contains(name.Split('.', 2)[0])) throw new InvalidOperationException("名称不能使用 Windows 保留设备名");
    }

    private const uint FoMove = 0x0001;
    private const uint FoCopy = 0x0002;
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation { public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags; [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShellFileOperation operation);
}
