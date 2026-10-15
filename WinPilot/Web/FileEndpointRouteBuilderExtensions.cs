using System.ComponentModel;
using WinPilot.Core;
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
                IFormCollection form;
                try { form = await request.ReadFormAsync(cancellationToken); }
                catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    logger.LogDebug(exception, "上传请求体超过大小限制");
                    return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "文件操作失败", detail: "上传请求体超过大小限制");
                }
                catch (InvalidDataException exception)
                {
                    logger.LogDebug(exception, "上传表单格式无效");
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "文件操作失败", detail: "上传表单格式无效");
                }
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
                        var failure = ClassifyFailure(exception);
                        LogFailure(logger, exception, failure, $"上传文件 {file.FileName}", directory);
                        results.Add(new { fileName = file.FileName, success = false, error = failure.Detail });
                    }
                }
                return Results.Ok(results);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException or IOException or Win32Exception)
            {
                return CreateFailureResult(logger, exception, "解析文件上传请求");
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
                return CreateFailureResult(loggerFactory.CreateLogger("FileEndpoints"), exception, "下载文件", string.Join(" | ", request.Paths ?? []));
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
            return CreateFailureResult(logger, exception, operation, path);
        }
    }

    internal static IResult CreateFailureResult(ILogger logger, Exception exception, string operation, string? path = null)
    {
        var failure = ClassifyFailure(exception);
        LogFailure(logger, exception, failure, operation, path);
        return Results.Problem(statusCode: failure.StatusCode, title: "文件操作失败", detail: failure.Detail);
    }

    internal static (int StatusCode, string Detail) ClassifyFailure(Exception exception)
    {
        if (exception is ShellFileOperationException shell)
        {
            return shell.ReturnCode switch
            {
                0x78 => (403, "无法访问此项目，没有足够的权限"),
                0x82 or 0x83 or 0x86 or 0x87 => (403, "项目所在位置为只读，无法修改"),
                0x7E or 0x80 => (409, "目标位置已存在同名文件或文件夹"),
                0x71 or 0x76 or 0x7D => (400, "源和目标位置无效"),
                0x74 or 0x10074 => (400, "不能对驱动器根目录执行此操作"),
                0x79 or 0x81 or 0xB7 => (400, "路径或文件名过长"),
                0x7C => (400, "路径无效"),
                0x85 => (400, "文件过大，目标位置无法存储"),
                0x72 or 0x73 or 0x75 or 0x7A or 0x84 or 0x88 or 0x402 or 0x10000 => (500, "文件操作失败"),
                _ => ClassifyWin32Failure(shell.ReturnCode)
            };
        }
        if (exception is ResourceNameConflictException) return (409, "目标位置已存在同名文件或文件夹");
        if (exception is FileEditConflictException) return (409, "文件在编辑期间已被其他程序修改");
        if (exception is FileNotFoundException or DirectoryNotFoundException) return (404, "文件或文件夹不存在");
        if (exception is UnauthorizedAccessException) return (403, "无法访问此项目，没有足够的权限");
        if (exception is PathTooLongException) return (400, "路径或文件名过长");
        if (exception is ArgumentException or NotSupportedException) return (400, "路径无效");
        if (exception is BadHttpRequestException) return (400, "文件操作失败");
        if (exception is InvalidOperationException invalidOperation) return (400, GetValidationMessage(invalidOperation));
        if (exception is Win32Exception win32) return ClassifyWin32Failure(win32.NativeErrorCode);
        if (exception is IOException io && ((uint)io.HResult & 0xFFFF0000u) == 0x80070000u)
            return ClassifyWin32Failure(io.HResult & 0xFFFF);
        return (500, "文件操作失败");
    }

    private static (int StatusCode, string Detail) ClassifyWin32Failure(int errorCode) => errorCode switch
    {
        5 or 65 => (403, "无法访问此项目，没有足够的权限"),
        19 => (403, "项目为只读，无法修改"),
        32 or 33 => (409, "文件正在被其他程序使用，请稍后重试"),
        80 or 183 => (409, "目标位置已存在同名文件或文件夹"),
        2 or 3 => (404, "文件或文件夹不存在"),
        87 or 123 => (400, "路径无效"),
        206 => (400, "路径或文件名过长"),
        _ => (500, "文件操作失败")
    };

    private static void LogFailure(ILogger logger, Exception exception, (int StatusCode, string Detail) failure, string operation, string? path = null)
    {
        var level = failure.StatusCode switch { 403 => LogLevel.Warning, >= 500 => LogLevel.Error, _ => LogLevel.Debug };
        if (exception is ShellFileOperationException shell)
            logger.Log(level, exception, "文件操作未完成：{Operation} {Path}，原因：{Reason}，Shell 返回码：{ReturnCode}，操作中止：{AnyOperationsAborted}",
                operation, path, exception.Message, shell.ReturnCode, shell.AnyOperationsAborted);
        else
            logger.Log(level, exception, failure.StatusCode switch
            {
                403 => "文件操作权限不足：{Operation} {Path}，原因：{Reason}",
                >= 500 => "文件操作失败：{Operation} {Path}，原因：{Reason}",
                _ => "文件操作未完成：{Operation} {Path}，原因：{Reason}"
            }, operation, path, exception.Message);
    }

    private static string GetValidationMessage(InvalidOperationException invalidOperation)
    {
        if (invalidOperation.Message.Contains("自身或其子目录", StringComparison.Ordinal)) return "无法将项目复制或移动到自身或其子目录中";
        if (invalidOperation.Message.Contains("有效的 Windows 绝对路径", StringComparison.Ordinal) || invalidOperation.Message.Contains("路径不是文件夹", StringComparison.Ordinal)) return "路径无效";
        if (invalidOperation.Message.Contains("路径不存在", StringComparison.Ordinal)) return "文件或文件夹不存在";
        if (invalidOperation.Message.Contains("名称", StringComparison.Ordinal)) return invalidOperation.Message;
        if (invalidOperation.Message.Contains("不支持编辑", StringComparison.Ordinal) || invalidOperation.Message.Contains("文件过大", StringComparison.Ordinal)) return invalidOperation.Message;
        return "文件操作失败";
    }
}
