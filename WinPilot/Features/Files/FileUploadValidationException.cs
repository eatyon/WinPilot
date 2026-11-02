namespace WinPilot.Features.Files;

internal class FileUploadValidationException(string message) : InvalidOperationException(message);

internal sealed class FileUploadChunkTooLargeException() : FileUploadValidationException("单次上传分块不能超过 1 MiB");

internal sealed class FileUploadConflictException(string message) : FileUploadValidationException(message);
