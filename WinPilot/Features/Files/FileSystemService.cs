using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using WinPilot.Core;
using WinPilot.Core.Files;

namespace WinPilot.Features.Files;

/// <summary>
/// 提供文件浏览、传输、编辑和下载，统一检查路径、目标冲突及 Windows 文件操作结果。
/// </summary>
public sealed class FileSystemService : IDisposable
{
    internal const int MaxUploadChunkBytes = 1024 * 1024;
    private static readonly TimeSpan UploadIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UploadCleanupInterval = TimeSpan.FromSeconds(30);
    private readonly object uploadSync = new();
    private readonly Dictionary<Guid, ChunkedUpload> uploads = new();
    private readonly ILogger<FileSystemService> logger;
    private readonly TimeProvider clock;
    private readonly Func<string, Stream> createUploadFile;
    private readonly Action<string, string> commitUploadFile;
    private ITimer? uploadCleanupTimer;
    private bool disposed;
    // 按路径散列分配固定数量的编辑锁，串行保存同一路径且不随访问文件数持续增加锁对象。
    private static readonly object[] EditGates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    public FileSystemService(ILogger<FileSystemService> logger) : this(logger, TimeProvider.System) { }

    internal FileSystemService(
        ILogger<FileSystemService> logger,
        TimeProvider clock,
        Func<string, Stream>? createUploadFile = null,
        Action<string, string>? commitUploadFile = null)
    {
        this.logger = logger;
        this.clock = clock;
        this.createUploadFile = createUploadFile ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        this.commitUploadFile = commitUploadFile ?? File.Move;
    }

    public IReadOnlyList<FileSystemRoot> GetRoots()
    {
        var roots = DriveInfo
            .GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new FileSystemRoot(drive.RootDirectory.FullName, BuildDriveName(drive), "驱动器"))
            .OrderBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roots;
    }

    public FileDirectoryResult GetDirectory(string path)
    {
        var directory = RequireDirectory(path);
        var entries = new DirectoryInfo(directory)
            .EnumerateFileSystemInfos()
            .Select(ToEntry)
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new FileDirectoryResult(directory, GetParent(directory), entries, IsSensitiveSystemPath(directory));
    }

    public FileUploadStartResult BeginUpload(BeginFileUploadRequest request)
    {
        CleanupExpiredUploads();
        var directory = RequireDirectory(request.Directory);
        if (request.Size < 0)
            throw new FileUploadValidationException("文件大小不能为负数");
        var fileName = Path.GetFileName((request.FileName ?? "").Replace('/', '\\'));
        ValidateName(fileName);
        var target = Path.Combine(directory, fileName);
        RejectExistingDestination(target);
        var id = Guid.NewGuid();
        var temporaryPath = Path.Combine(directory, $".winpilot-upload-{id:N}.tmp");
        lock (uploadSync) ObjectDisposedException.ThrowIf(disposed, this);
        var created = false;
        var registered = false;
        try
        {
            using (createUploadFile(temporaryPath))
            {
                created = true;
            }
            lock (uploadSync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                uploadCleanupTimer ??= clock.CreateTimer(_ => CleanupExpiredUploads(), null, UploadCleanupInterval, UploadCleanupInterval);
                uploads.Add(id, new(id, target, temporaryPath, request.Size, clock.GetUtcNow()));
                registered = true;
            }
            return new FileUploadStartResult(id.ToString("N"));
        }
        finally
        {
            // 创建期间可能已经停机；登记失败只清理本次成功创建的文件。
            if (created && !registered)
            {
                lock (uploadSync) StopUploadCleanupTimerIfIdle();
                TryDelete(temporaryPath);
            }
        }
    }

    public async Task AppendUploadChunkAsync(FileUploadChunkRequest request, Stream content, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(request.UploadId, "N", out var id) || request.Offset < 0)
            throw new FileUploadValidationException("上传分块参数无效");
        ChunkedUpload upload;
        lock (uploadSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!uploads.TryGetValue(id, out upload!) || upload.State != UploadState.Receiving)
                throw new FileUploadValidationException("上传会话不存在或已关闭");
            upload.Users++;
        }
        var entered = false;
        try
        {
            await upload.Gate.WaitAsync(cancellationToken);
            entered = true;
            lock (uploadSync)
            {
                if (upload.State != UploadState.Receiving) throw new FileUploadValidationException("上传会话不存在或已关闭");
            }
            if (request.Offset != upload.Received) throw new FileUploadValidationException("上传分块顺序无效");
            await using var output = new FileStream(upload.TemporaryPath, FileMode.Open, FileAccess.Write, FileShare.None, 128 * 1024, true);
            output.Position = request.Offset;
            var buffer = new byte[128 * 1024];
            var total = 0L;
            while (total <= MaxUploadChunkBytes)
            {
                var read = await content.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > MaxUploadChunkBytes) throw new FileUploadChunkTooLargeException();
                if (total > upload.Size - upload.Received)
                    throw new FileUploadValidationException("上传内容超过声明的文件大小");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (total == 0) throw new FileUploadValidationException("上传分块不能为空");
            await output.FlushAsync(cancellationToken);
            upload.Received += total;
            lock (uploadSync) upload.LastActivity = clock.GetUtcNow();
        }
        finally
        {
            if (entered) upload.Gate.Release();
            ReleaseUpload(upload);
        }
    }

    public async Task CompleteUploadAsync(CompleteFileUploadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(request.UploadId, "N", out var id))
            throw new FileUploadValidationException("上传会话不存在或已关闭");
        ChunkedUpload upload;
        lock (uploadSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!uploads.TryGetValue(id, out upload!) || upload.State != UploadState.Receiving)
                throw new FileUploadValidationException("上传会话不存在或已关闭");
            upload.State = UploadState.Completing;
            upload.CompletionCancellation = new();
            upload.Users++;
        }
        var entered = false;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, upload.CompletionCancellation!.Token);
            try
            {
                await upload.Gate.WaitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FileUploadValidationException("上传已取消");
            }
            entered = true;
            lock (uploadSync) ObjectDisposedException.ThrowIf(disposed, this);
            if (upload.Received != upload.Size) throw new FileUploadValidationException("上传文件不完整");
            RejectExistingDestination(upload.TargetPath);
            lock (uploadSync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (upload.State == UploadState.Cancelled) throw new FileUploadValidationException("上传已取消");
                // 提交承诺与取消共用状态锁；原生移动不在锁内执行，提交后不回滚目标文件。
                upload.State = UploadState.Committing;
            }
            commitUploadFile(upload.TemporaryPath, upload.TargetPath);
            lock (uploadSync)
            {
                upload.State = UploadState.Completed;
            }
            logger.LogInformation("文件操作：上传文件 {Path}", upload.TargetPath);
        }
        finally
        {
            lock (uploadSync)
            {
                if (upload.State != UploadState.Completed) upload.State = UploadState.Cancelled;
            }
            if (entered) upload.Gate.Release();
            ReleaseUpload(upload);
        }
    }

    public void CancelUpload(string uploadId)
    {
        if (!Guid.TryParseExact(uploadId, "N", out var id)) return;
        ChunkedUpload upload;
        lock (uploadSync)
        {
            if (!uploads.TryGetValue(id, out upload!) || upload.State == UploadState.Cancelled) return;
            if (upload.State is UploadState.Committing or UploadState.Completed)
                throw new FileUploadConflictException("文件已进入提交阶段，无法取消上传");
            upload.State = UploadState.Cancelled;
            upload.Users++;
        }
        try { upload.CompletionCancellation?.Cancel(); }
        finally { ReleaseUpload(upload); }
    }

    private void CleanupExpiredUploads()
    {
        List<ChunkedUpload> expired = [];
        lock (uploadSync)
        {
            if (disposed) return;
            var threshold = clock.GetUtcNow() - UploadIdleTimeout;
            foreach (var upload in uploads.Values)
            {
                if (upload.State == UploadState.Receiving && upload.Users == 0 && upload.LastActivity <= threshold)
                {
                    upload.State = UploadState.Cancelled;
                    expired.Add(upload);
                }
            }
            foreach (var upload in expired) uploads.Remove(upload.Id);
            StopUploadCleanupTimerIfIdle();
        }
        foreach (var upload in expired) CleanupUpload(upload);
    }

    private void StopUploadCleanupTimerIfIdle()
    {
        if (uploads.Count != 0) return;
        uploadCleanupTimer?.Dispose();
        uploadCleanupTimer = null;
    }

    public void Dispose()
    {
        List<ChunkedUpload> cleanup = [];
        lock (uploadSync)
        {
            if (disposed) return;
            disposed = true;
            uploadCleanupTimer?.Dispose();
            uploadCleanupTimer = null;
            foreach (var upload in uploads.Values)
            {
                if (upload.State != UploadState.Committing) upload.State = UploadState.Cancelled;
                if (upload.Users == 0) cleanup.Add(upload);
            }
            foreach (var upload in cleanup) uploads.Remove(upload.Id);
        }
        foreach (var upload in cleanup) CleanupUpload(upload);
    }

    private void ReleaseUpload(ChunkedUpload upload)
    {
        var cleanup = false;
        lock (uploadSync)
        {
            upload.Users--;
            if (upload.State != UploadState.Receiving && upload.Users == 0)
            {
                uploads.Remove(upload.Id);
                cleanup = true;
                StopUploadCleanupTimerIfIdle();
            }
        }
        if (cleanup) CleanupUpload(upload);
    }

    private void CleanupUpload(ChunkedUpload upload)
    {
        // 已登记的使用者全部退出后才释放信号量；关闭不等待仍在读取请求体的分块。
        upload.Gate.Dispose();
        upload.CompletionCancellation?.Dispose();
        TryDelete(upload.TemporaryPath);
    }

    private enum UploadState { Receiving, Completing, Committing, Completed, Cancelled }

    private sealed class ChunkedUpload(
        Guid id,
        string targetPath,
        string temporaryPath,
        long size,
        DateTimeOffset lastActivity)
    {
        public Guid Id { get; } = id;
        public string TargetPath { get; } = targetPath;
        public string TemporaryPath { get; } = temporaryPath;
        public long Size { get; } = size;
        public long Received { get; set; }
        public DateTimeOffset LastActivity { get; set; } = lastActivity;
        public UploadState State { get; set; }
        public CancellationTokenSource? CompletionCancellation { get; set; }
        public int Users { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
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
        using (new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }
        logger.LogInformation("文件操作：新建文件 {Path}", target);
    }

    public void Rename(RenameFileSystemItemRequest request)
    {
        var source = RequireExisting(request.Path);
        RejectRoot(source);
        ValidateName(request.Name);
        var destination = Path.Combine(Path.GetDirectoryName(source)!, request.Name);
        RejectExistingDestination(destination);
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
        logger.LogInformation("文件操作：重命名 {Source} 为 {Destination}", source, destination);
    }

    /// <summary>
    /// 通过 Windows Shell 尝试将选中项目移入回收站，并拒绝操作驱动器根目录。
    /// </summary>
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
        logger.LogInformation(
            "文件操作：复制 {Count} 项到 {DestinationDirectory}：{Paths}",
            sources.Count,
            destinationDirectory,
            string.Join(" | ", sources));
    }

    public void Move(TransferFileSystemItemsRequest request)
    {
        var sources = RequireDistinctPaths(request.SourcePaths);
        var destinationDirectory = RequireDirectory(request.DestinationDirectory);
        ValidateTransfer(sources, destinationDirectory);
        ExecuteShellOperation(FoMove, sources, destinationDirectory, 0, "移动");
        logger.LogInformation(
            "文件操作：移动 {Count} 项到 {DestinationDirectory}：{Paths}",
            sources.Count,
            destinationDirectory,
            string.Join(" | ", sources));
    }

    public EditFileSystemItemResult ReadForEdit(string path)
    {
        var fullPath = RequireExisting(path);
        if (Directory.Exists(fullPath))
        {
            throw new InvalidOperationException("此项目不支持编辑");
        }
        var info = new FileInfo(fullPath);
        if (info.Length > MaxEditableFileSize)
        {
            throw new InvalidOperationException("文件过大，不支持在此处编辑");
        }
        using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxEditableFileSize)
        {
            throw new InvalidOperationException("文件过大，不支持在此处编辑");
        }
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        info.Refresh();
        var (encoding, preambleLength) = DetectTextEncoding(bytes);
        string content;
        try
        {
            content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("此文件不支持编辑");
        }
        return new EditFileSystemItemResult(fullPath, content, info.Length, info.LastWriteTimeUtc, EncodingName(encoding));
    }

    /// <summary>
    /// 按读取时的长度与修改时间检测冲突，保留支持的原编码，经临时文件替换完成保存。
    /// </summary>
    public void SaveEdit(EditFileSystemItemRequest request)
    {
        var path = RequireExisting(request.Path);
        var editGate = EditGates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % (uint)EditGates.Length];
        lock (editGate)
        {
            SaveEditCore(request, path);
        }
    }

    private void SaveEditCore(EditFileSystemItemRequest request, string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException("此项目不支持编辑");
        }
        var info = new FileInfo(path);
        if (info.Length != request.OriginalLength || info.LastWriteTimeUtc != request.OriginalLastWriteTime)
        {
            throw new FileEditConflictException("文件在编辑期间已被其他程序修改");
        }
        var encoding = ParseTextEncoding(request.Encoding);
        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[3];
            var length = input.Read(header);
            if (header[..length].StartsWith(Encoding.UTF8.GetPreamble()))
            {
                encoding = new UTF8Encoding(true, true);
            }
        }
        if (encoding.GetByteCount(request.Content) + encoding.GetPreamble().Length > MaxEditableFileSize)
        {
            throw new InvalidOperationException("文件过大，不支持在此处编辑");
        }
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
            // 写临时文件期间原文件仍可能被外部程序修改，替换前再核对一次编辑基线。
            info.Refresh();
            if (info.Length != request.OriginalLength || info.LastWriteTimeUtc != request.OriginalLastWriteTime)
            {
                throw new FileEditConflictException("文件在编辑期间已被其他程序修改");
            }
            File.Replace(temporaryPath, path, null);
            logger.LogInformation("文件操作：编辑保存 {Path}", path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private const long MaxEditableFileSize = 10 * 1024 * 1024;

    private static (Encoding Encoding, int PreambleLength) DetectTextEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            return (new UTF8Encoding(true, true), Encoding.UTF8.GetPreamble().Length);
        }
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            return (Encoding.Unicode, Encoding.Unicode.GetPreamble().Length);
        }
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            throw new InvalidOperationException("此文件不支持编辑");
        }
        return (new UTF8Encoding(false, true), 0);
    }

    private static string EncodingName(Encoding encoding) =>
        encoding.CodePage == Encoding.Unicode.CodePage ? "utf-16" : "utf-8";

    private static Encoding ParseTextEncoding(string? value) =>
        string.Equals(value, "utf-16", StringComparison.OrdinalIgnoreCase)
            ? Encoding.Unicode
            : string.Equals(value, "utf-8", StringComparison.OrdinalIgnoreCase)
                ? new UTF8Encoding(false, true)
                : throw new InvalidOperationException("此文件不支持编辑");

    /// <summary>
    /// 单文件直接返回读取流，多项目或目录先生成 ZIP；调用方负责释放返回流。
    /// </summary>
    public Task<FileDownloadArtifact> CreateDownloadAsync(
        DownloadFileSystemItemsRequest request,
        CancellationToken cancellationToken)
        =>
        CreateDownloadAsync(request, cancellationToken, Path.GetTempPath());

    internal async Task<FileDownloadArtifact> CreateDownloadAsync(
        DownloadFileSystemItemsRequest request,
        CancellationToken cancellationToken,
        string temporaryDirectory)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = RequireDistinctPaths(request.Paths);
        if (paths.Count == 1 && File.Exists(paths[0]))
        {
            var path = paths[0];
            return new FileDownloadArtifact(
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan),
                Path.GetFileName(path),
                "application/octet-stream");
        }

        var temporaryPath = Path.Combine(temporaryDirectory, $"WinPilot-download-{Guid.NewGuid():N}.zip");
        try
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var archive = await ZipArchive.CreateAsync(
                    output,
                    ZipArchiveMode.Create,
                    leaveOpen: true,
                    entryNameEncoding: null,
                    cancellationToken: cancellationToken))
                {
                    foreach (var path in paths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                        if (string.IsNullOrWhiteSpace(rootName))
                        {
                            throw new InvalidOperationException("无法下载驱动器根目录");
                        }
                        if (File.Exists(path))
                        {
                            await AddFileToArchiveAsync(archive, path, rootName, usedNames, cancellationToken);
                        }
                        else
                        {
                            await AddArchiveDirectoryAsync(archive, path, rootName, usedNames, cancellationToken);
                        }
                    }
                }
            }
            // 将临时 ZIP 的清理绑定到下载流关闭，包括正常结束和中途断开。
            var stream = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            return new FileDownloadArtifact(
                stream,
                paths.Count == 1
                    ? Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0])) + ".zip"
                    : "Download.zip",
                "application/zip");
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task AddArchiveDirectoryAsync(
        ZipArchive archive,
        string directory,
        string relativePath,
        HashSet<string> usedNames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("文件夹包含目录链接，无法安全打包下载");
        }
        AddArchiveEntryName(usedNames, relativePath + "/");
        archive.CreateEntry(relativePath + "/");
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childPath = relativePath + "/" + item.Name;
            if (item is DirectoryInfo childDirectory)
            {
                await AddArchiveDirectoryAsync(archive, childDirectory.FullName, childPath, usedNames, cancellationToken);
            }
            else
            {
                await AddFileToArchiveAsync(archive, item.FullName, childPath, usedNames, cancellationToken);
            }
        }
    }

    private static async Task AddFileToArchiveAsync(
        ZipArchive archive,
        string source,
        string relativePath,
        HashSet<string> usedNames,
        CancellationToken cancellationToken)
    {
        AddArchiveEntryName(usedNames, relativePath);
        var entry = archive.CreateEntry(relativePath, CompressionLevel.Fastest);
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var entryStream = await entry.OpenAsync(cancellationToken);
        await input.CopyToAsync(entryStream, 128 * 1024, cancellationToken);
    }

    private static void AddArchiveEntryName(HashSet<string> usedNames, string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.Contains("../", StringComparison.Ordinal) || !usedNames.Add(normalized.TrimEnd('/')))
        {
            throw new InvalidOperationException("下载内容存在路径冲突");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "无法清理文件操作临时文件 {Path}", path);
        }
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
        return new FileSystemEntry(
            info.Name,
            info.FullName,
            isDirectory,
            attributes.HasFlag(FileAttributes.ReparsePoint),
            IsSensitiveSystemPath(info.FullName),
            type,
            isDirectory ? null : ((FileInfo)info).Length,
            info.LastWriteTimeUtc);
    }

    private static string? GetParent(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrWhiteSpace(root)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
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
        if (input is null || input.Count == 0)
        {
            throw new InvalidOperationException("请至少选择一项");
        }
        var paths = input.Select(RequireExisting).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length != input.Count)
        {
            throw new InvalidOperationException("操作路径不能重复");
        }
        return paths;
    }

    private static void ValidateTransfer(IReadOnlyList<string> sources, string destinationDirectory)
    {
        foreach (var source in sources)
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            RejectExistingDestination(destination);
            if (Directory.Exists(source))
            {
                RejectNestedDestination(source, destinationDirectory);
            }
        }
    }

    private static void RejectNestedDestination(string source, string destination)
    {
        if (IsSameOrChildPath(source, destination))
        {
            throw new InvalidOperationException("不能复制或移动文件夹到自身或其子目录");
        }
    }

    private static bool IsSameOrChildPath(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string RequireDirectory(string? path)
    {
        var fullPath = RequireExisting(path);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException("路径不是文件夹");
        }
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
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("请输入有效的 Windows 绝对路径");
        }
        return Path.GetFullPath(path);
    }

    private static void RejectExistingDestination(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new ResourceNameConflictException($"目标已存在：{Path.GetFileName(path)}");
        }
    }

    private static void RejectRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrWhiteSpace(root)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不能对驱动器根目录执行此操作");
        }
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

    private static void ExecuteShellOperation(
        uint operationType,
        IReadOnlyList<string> sources,
        string? destinationDirectory,
        ushort additionalFlags,
        string operationName)
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
        if (errorCode != 0)
        {
            throw new ShellFileOperationException(
                $"无法{operationName}：{new Win32Exception(errorCode).Message}",
                errorCode,
                operation.fAnyOperationsAborted);
        }
        if (operation.fAnyOperationsAborted)
        {
            throw new ShellFileOperationException($"{operationName}操作已取消", errorCode, operation.fAnyOperationsAborted);
        }
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            throw new InvalidOperationException("名称不能为空，且不能使用 . 或 ..");
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
        {
            throw new InvalidOperationException("名称包含 Windows 文件名不支持的字符");
        }
        if (ReservedFileNames.Contains(name.Split('.', 2)[0]))
        {
            throw new InvalidOperationException("名称不能使用 Windows 保留设备名");
        }
    }

    private const uint FoMove = 0x0001;
    private const uint FoCopy = 0x0002;
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShellFileOperation operation);
}
