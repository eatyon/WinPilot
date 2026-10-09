using System.ComponentModel;
using WinPilot.Core.Files;
using WinPilot.Features.Files;

namespace WinPilot.Web;

public static class FileEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/files/roots", (FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => Results.Ok(service.GetRoots()), loggerFactory.CreateLogger("FileEndpoints"), "读取驱动器"));
        endpoints.MapGet("/api/files/directory", (string? path, FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => Results.Ok(service.GetDirectory(path ?? "")), loggerFactory.CreateLogger("FileEndpoints"), "读取文件夹", path));
        endpoints.MapPost("/api/files/upload", async (HttpRequest request, FileSystemService service, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("FileEndpoints");
            try
            {
                var form = await request.ReadFormAsync(cancellationToken);
                var directory = form["directory"].ToString();
                if (string.IsNullOrWhiteSpace(directory)) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "文件操作失败", detail: "路径无效");
                var results = new List<object>(form.Files.Count);
                foreach (var file in form.Files)
                {
                    try
                    {
                        await service.UploadAsync(directory, file, cancellationToken);
                        results.Add(new { fileName = file.FileName, success = true, error = (string?)null });
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException or IOException or Win32Exception)
                    {
                        LogFailure(logger, exception, $"上传文件 {file.FileName}", directory);
                        results.Add(new { fileName = file.FileName, success = false, error = GetUserMessage(exception) });
                    }
                }
                return Results.Ok(results);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException or IOException or Win32Exception)
            {
                LogFailure(logger, exception, "解析文件上传请求");
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "文件操作失败", detail: GetUserMessage(exception));
            }
        });
        endpoints.MapPost("/api/files/download", async (DownloadFileSystemItemsRequest request, FileSystemService service, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            try
            {
                var artifact = await service.CreateDownloadAsync(request, cancellationToken);
                return Results.File(artifact.Content, artifact.ContentType, artifact.FileName, enableRangeProcessing: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException or IOException or Win32Exception)
            {
                LogFailure(loggerFactory.CreateLogger("FileEndpoints"), exception, "下载文件", string.Join(" | ", request.Paths ?? []));
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "文件操作失败", detail: GetUserMessage(exception));
            }
        });
        endpoints.MapPost("/api/files/folder", (CreateFileSystemItemRequest request, FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => { service.CreateDirectory(request); return Results.NoContent(); }, loggerFactory.CreateLogger("FileEndpoints"), "新建文件夹", request.ParentPath));
        endpoints.MapPost("/api/files/file", (CreateFileSystemItemRequest request, FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => { service.CreateFile(request); return Results.NoContent(); }, loggerFactory.CreateLogger("FileEndpoints"), "新建文件", request.ParentPath));
        endpoints.MapPost("/api/files/rename", (RenameFileSystemItemRequest request, FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => { service.Rename(request); return Results.NoContent(); }, loggerFactory.CreateLogger("FileEndpoints"), "重命名", request.Path));
        endpoints.MapPost("/api/files/delete", (DeleteFileSystemItemsRequest request, FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => { service.Delete(request); return Results.NoContent(); }, loggerFactory.CreateLogger("FileEndpoints"), "移入回收站", string.Join(" | ", request.Paths ?? [])));
        endpoints.MapPost("/api/files/copy", (TransferFileSystemItemsRequest request, FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => { service.Copy(request); return Results.NoContent(); }, loggerFactory.CreateLogger("FileEndpoints"), "复制文件", string.Join(" | ", request.SourcePaths ?? [])));
        endpoints.MapPost("/api/files/move", (TransferFileSystemItemsRequest request, FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => { service.Move(request); return Results.NoContent(); }, loggerFactory.CreateLogger("FileEndpoints"), "移动文件", string.Join(" | ", request.SourcePaths ?? [])));
        endpoints.MapGet("/api/files/edit", (string? path, FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => Results.Ok(service.ReadForEdit(path ?? "")), loggerFactory.CreateLogger("FileEndpoints"), "读取文件", path));
        endpoints.MapPost("/api/files/edit", (EditFileSystemItemRequest request, FileSystemService service, ILoggerFactory loggerFactory) => Execute(() => { service.SaveEdit(request); return Results.NoContent(); }, loggerFactory.CreateLogger("FileEndpoints"), "保存文件", request.Path));
        return endpoints;
    }

    private static IResult Execute(Func<IResult> action, ILogger logger, string operation, string? path = null)
    {
        try { return action(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException or IOException or Win32Exception)
        {
            LogFailure(logger, exception, operation, path);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "文件操作失败", detail: GetUserMessage(exception));
        }
    }

    private static void LogFailure(ILogger logger, Exception exception, string operation, string? path = null)
    {
        if (exception is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } || exception is IOException accessException && (accessException.HResult & 0xFFFF) == 5)
            logger.LogWarning("文件操作权限不足：{Operation} {Path}，原因：{Reason}", operation, path, exception.Message);
        else if (exception is InvalidOperationException or ArgumentException or NotSupportedException or FileNotFoundException or DirectoryNotFoundException || exception is IOException sharingException && IsSharingViolation(sharingException))
            logger.LogDebug("文件操作未完成：{Operation} {Path}，原因：{Reason}", operation, path, exception.Message);
        else
            logger.LogError(exception, "文件操作失败：{Operation} {Path}", operation, path);
    }

    private static string GetUserMessage(Exception exception)
    {
        if (exception is UnauthorizedAccessException || exception is Win32Exception { NativeErrorCode: 5 } || exception is IOException ioException && (ioException.HResult & 0xFFFF) == 5) return "无法访问此项目，没有足够的权限";
        if (exception is FileNotFoundException or DirectoryNotFoundException) return "文件或文件夹不存在";
        if (exception is IOException sharingException && IsSharingViolation(sharingException)) return "文件正在被其他程序使用，请稍后重试";
        if (exception is ArgumentException || exception is NotSupportedException) return "路径无效";
        if (exception is InvalidOperationException invalidOperation)
        {
            if (invalidOperation.Message.Contains("目标已存在", StringComparison.Ordinal)) return "目标位置已存在同名文件或文件夹";
            if (invalidOperation.Message.Contains("自身或其子目录", StringComparison.Ordinal)) return "无法将项目复制或移动到自身或其子目录中";
            if (invalidOperation.Message.Contains("有效的 Windows 绝对路径", StringComparison.Ordinal) || invalidOperation.Message.Contains("路径不是文件夹", StringComparison.Ordinal)) return "路径无效";
            if (invalidOperation.Message.Contains("路径不存在", StringComparison.Ordinal)) return "文件或文件夹不存在";
            if (invalidOperation.Message.Contains("名称", StringComparison.Ordinal)) return invalidOperation.Message;
            if (invalidOperation.Message.Contains("编辑期间已被其他程序修改", StringComparison.Ordinal)) return invalidOperation.Message;
            if (invalidOperation.Message.Contains("不支持编辑", StringComparison.Ordinal) || invalidOperation.Message.Contains("文件过大", StringComparison.Ordinal)) return invalidOperation.Message;
        }
        return "文件操作失败";
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is 32 or 33;
    }
}
