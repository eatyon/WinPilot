using System.Net;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Core.Files;
using WinPilot.Features.Files;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class FileEndpointTest : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinPilot.File.Endpoint.Test", Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<FileSystemService>();
        app = builder.Build();
        app.MapFileEndpoints();
        await app.StartAsync();
        client = app.GetTestClient();
    }

    [Fact]
    public async Task CreateBrowseRenameEditAndDownloadPreserveTheFile()
    {
        using var created = await client.PostAsJsonAsync("/api/files/file", new CreateFileSystemItemRequest(root, "file.txt"));
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        using var duplicate = await client.PostAsJsonAsync("/api/files/file", new CreateFileSystemItemRequest(root, "file.txt"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "original", new UTF8Encoding(true));
        var directory = await client.GetFromJsonAsync<FileDirectoryResult>("/api/files/directory?path=" + Uri.EscapeDataString(root));
        Assert.Equal("file.txt", Assert.Single(directory!.Entries).Name);
        var original = await client.GetFromJsonAsync<EditFileSystemItemResult>("/api/files/edit?path=" + Uri.EscapeDataString(path));
        using var saved = await client.PostAsJsonAsync(
            "/api/files/edit",
            new EditFileSystemItemRequest(path, "saved", original!.Length, original.LastWriteTime, original.Encoding));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        using var renamed = await client.PostAsJsonAsync("/api/files/rename", new RenameFileSystemItemRequest(path, "renamed.txt"));
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        var renamedPath = Path.Combine(root, "renamed.txt");
        using var download = await client.PostAsJsonAsync("/api/files/download", new DownloadFileSystemItemsRequest([renamedPath]));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(File.ReadAllBytes(renamedPath), await download.Content.ReadAsByteArrayAsync());
        Assert.True(File.ReadAllBytes(renamedPath).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    }

    [Fact]
    public async Task ChunkedUploadWritesFileAndRejectsInvalidSize()
    {
        using var started = await client.PostAsJsonAsync(
            "/api/files/upload/start",
            new BeginFileUploadRequest(root, "chunked.txt", 11));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var upload = await started.Content.ReadFromJsonAsync<FileUploadStartResult>();
        Assert.NotNull(upload);

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello world"));
        content.Headers.ContentType = new("application/octet-stream");
        using var chunk = await client.PostAsync(
            $"/api/files/upload/chunk?uploadId={upload!.UploadId}&offset=0",
            content);
        Assert.Equal(HttpStatusCode.NoContent, chunk.StatusCode);
        using var completed = await client.PostAsJsonAsync(
            "/api/files/upload/complete",
            new CompleteFileUploadRequest(upload.UploadId));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(root, "chunked.txt")));

        using var invalidSize = await client.PostAsJsonAsync(
            "/api/files/upload/start",
            new BeginFileUploadRequest(root, "invalid.bin", -1));
        Assert.Equal(HttpStatusCode.BadRequest, invalidSize.StatusCode);
        Assert.False(File.Exists(Path.Combine(root, "invalid.bin")));
    }

    [Fact]
    public async Task EmptyFileCanBeCompletedWithoutChunks()
    {
        using var started = await client.PostAsJsonAsync("/api/files/upload/start", new BeginFileUploadRequest(root, "empty.bin", 0));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var upload = await started.Content.ReadFromJsonAsync<FileUploadStartResult>();
        Assert.NotNull(upload);
        using var completed = await client.PostAsJsonAsync("/api/files/upload/complete", new CompleteFileUploadRequest(upload.UploadId));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.Empty(File.ReadAllBytes(Path.Combine(root, "empty.bin")));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
    }

    [Fact]
    public async Task CompletedUploadReleasesSessionAndCannotBeCompletedAgain()
    {
        using var started = await client.PostAsJsonAsync("/api/files/upload/start", new BeginFileUploadRequest(root, "empty.bin", 0));
        var upload = await started.Content.ReadFromJsonAsync<FileUploadStartResult>();
        Assert.NotNull(upload);
        using var completed = await client.PostAsJsonAsync("/api/files/upload/complete", new CompleteFileUploadRequest(upload.UploadId));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        using var retried = await client.PostAsJsonAsync("/api/files/upload/complete", new CompleteFileUploadRequest(upload.UploadId));
        Assert.Equal(HttpStatusCode.BadRequest, retried.StatusCode);
        using var cancelled = await client.PostAsJsonAsync("/api/files/upload/cancel", new CompleteFileUploadRequest(upload.UploadId));
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.True(File.Exists(Path.Combine(root, "empty.bin")));
    }

    [Fact]
    public async Task CancelledUploadStaysCancelledAndRepeatedCancellationSucceeds()
    {
        using var started = await client.PostAsJsonAsync("/api/files/upload/start", new BeginFileUploadRequest(root, "empty.bin", 0));
        var upload = await started.Content.ReadFromJsonAsync<FileUploadStartResult>();
        Assert.NotNull(upload);
        foreach (var id in new[] { upload.UploadId, upload.UploadId, "invalid" })
        {
            using var cancelled = await client.PostAsJsonAsync("/api/files/upload/cancel", new CompleteFileUploadRequest(id));
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        }
        using var completed = await client.PostAsJsonAsync("/api/files/upload/complete", new CompleteFileUploadRequest(upload.UploadId));
        Assert.Equal(HttpStatusCode.BadRequest, completed.StatusCode);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingUploadNameReturnsValidationError(string? name)
    {
        using var response = await client.PostAsJsonAsync("/api/files/upload/start", new { directory = root, fileName = name, size = 1 });
        await AssertProblem(response, HttpStatusCode.BadRequest, "名称不能为空，且不能使用 . 或 ..");
        Assert.Empty(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrNullUploadSizeReturns400WithoutCreatingFiles(bool explicitNull)
    {
        object body = explicitNull
            ? new { directory = root, fileName = "file.bin", size = (long?)null }
            : new { directory = root, fileName = "file.bin" };
        using var response = await client.PostAsJsonAsync("/api/files/upload/start", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedChunksReturn413WithAndWithoutContentLength(bool knownLength)
    {
        using var started = await client.PostAsJsonAsync("/api/files/upload/start", new BeginFileUploadRequest(root, "large.bin", 2 * FileSystemService.MaxUploadChunkBytes));
        var upload = await started.Content.ReadFromJsonAsync<FileUploadStartResult>();
        var bytes = new byte[FileSystemService.MaxUploadChunkBytes + 1];
        using HttpContent content = knownLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
        content.Headers.ContentType = new("application/octet-stream");
        if (knownLength) Assert.Equal(bytes.Length, content.Headers.ContentLength);
        else Assert.Null(content.Headers.ContentLength);
        using var response = await client.PostAsync($"/api/files/upload/chunk?uploadId={upload!.UploadId}&offset=0", content);
        await AssertProblem(response, HttpStatusCode.RequestEntityTooLarge, "单次上传分块不能超过 1 MiB");
        using var cancelled = await client.PostAsJsonAsync("/api/files/upload/cancel", new CompleteFileUploadRequest(upload.UploadId));
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task UploadValidationResponsesPreserveSpecificReasons()
    {
        using var started = await client.PostAsJsonAsync("/api/files/upload/start", new BeginFileUploadRequest(root, "file.bin", 1));
        var upload = await started.Content.ReadFromJsonAsync<FileUploadStartResult>();
        using var incomplete = await client.PostAsJsonAsync("/api/files/upload/complete", new CompleteFileUploadRequest(upload!.UploadId));
        await AssertProblem(incomplete, HttpStatusCode.BadRequest, "上传文件不完整");
        using var missing = await client.PostAsJsonAsync("/api/files/upload/complete", new CompleteFileUploadRequest(upload.UploadId));
        await AssertProblem(missing, HttpStatusCode.BadRequest, "上传会话不存在或已关闭");
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task ChunkedUploadRequiresSequentialNonEmptyChunks()
    {
        using var started = await client.PostAsJsonAsync(
            "/api/files/upload/start",
            new BeginFileUploadRequest(root, "ordered.bin", 3));
        var upload = await started.Content.ReadFromJsonAsync<FileUploadStartResult>();
        Assert.NotNull(upload);

        using var empty = new ByteArrayContent([]);
        empty.Headers.ContentType = new("application/octet-stream");
        using var emptyResponse = await client.PostAsync(
            $"/api/files/upload/chunk?uploadId={upload!.UploadId}&offset=0",
            empty);
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);

        using var outOfOrder = new ByteArrayContent([1, 2, 3]);
        outOfOrder.Headers.ContentType = new("application/octet-stream");
        using var outOfOrderResponse = await client.PostAsync(
            $"/api/files/upload/chunk?uploadId={upload.UploadId}&offset=1",
            outOfOrder);
        Assert.Equal(HttpStatusCode.BadRequest, outOfOrderResponse.StatusCode);

        using var cancel = await client.PostAsJsonAsync(
            "/api/files/upload/cancel",
            new CompleteFileUploadRequest(upload.UploadId));
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
    }

    [Theory]
    [InlineData("directory", "GET")]
    [InlineData("edit", "GET")]
    [InlineData("edit", "POST")]
    [InlineData("download", "POST")]
    [InlineData("file", "POST")]
    [InlineData("folder", "POST")]
    [InlineData("rename", "POST")]
    [InlineData("delete", "POST")]
    [InlineData("copy", "POST")]
    [InlineData("move", "POST")]
    public async Task MissingFileOrDirectoryReturns404(string operation, string method)
    {
        var missing = Path.Combine(root, "missing.txt");
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            $"/api/files/{operation}" +
            (method == "GET" ? "?path=" + Uri.EscapeDataString(missing) : ""));
        if (method == "POST")
        {
            object body = operation switch
            {
                "file" or "folder" => new CreateFileSystemItemRequest(missing, "new.txt"),
                "rename" => new RenameFileSystemItemRequest(missing, "new.txt"),
                "delete" => new DeleteFileSystemItemsRequest([missing]),
                "copy" or "move" => new TransferFileSystemItemsRequest([missing], root),
                "download" => new DownloadFileSystemItemsRequest([missing]),
                _ => new EditFileSystemItemRequest(missing, "replacement", 0, DateTimeOffset.UtcNow, "utf-8")
            };
            request.Content = JsonContent.Create(body);
        }
        using var response = await client.SendAsync(request);
        await AssertProblem(response, HttpStatusCode.NotFound, "文件或文件夹不存在");
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task DeniedDirectoryEnumerationReturns403AndRestoresPermissions()
    {
        var directory = new DirectoryInfo(Path.Combine(root, "denied"));
        directory.Create();
        var original = directory.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var denied = directory.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("测试无法获取当前用户 SID");
        denied.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            using var response = await client.GetAsync("/api/files/directory?path=" + Uri.EscapeDataString(directory.FullName));
            await AssertProblem(response, HttpStatusCode.Forbidden, "无法访问此项目，没有足够的权限");
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            directory.SetAccessControl(restored);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedFileReadOrDownloadReturns409(bool download)
    {
        var path = Path.Combine(root, "locked.txt");
        File.WriteAllText(path, "original");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var response = download
                ? await client.PostAsJsonAsync("/api/files/download", new DownloadFileSystemItemsRequest([path]))
                : await client.GetAsync("/api/files/edit?path=" + Uri.EscapeDataString(path));
            await AssertProblem(response, HttpStatusCode.Conflict, "文件正在被其他程序使用，请稍后重试");
        }
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("rename")]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task ExistingTargetReturns409WithoutChangingSourceOrDestination(string operation)
    {
        var source = Path.Combine(root, "source.txt");
        File.WriteAllText(source, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(destination);
        var target = Path.Combine(destination, "source.txt");
        File.WriteAllText(target, "keep");
        var existing = Path.Combine(root, "exists.txt");
        File.WriteAllText(existing, "keep");
        object body = operation switch
        {
            "folder" => new CreateFileSystemItemRequest(root, "exists.txt"),
            "rename" => new RenameFileSystemItemRequest(source, "exists.txt"),
            _ => new TransferFileSystemItemsRequest([source], destination)
        };
        using var response = await client.PostAsJsonAsync($"/api/files/{operation}", body);
        await AssertProblem(response, HttpStatusCode.Conflict, "目标位置已存在同名文件或文件夹");
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("keep", File.ReadAllText(target));
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditLengthOrTimestampConflictReturns409WithoutOverwritingExternalChanges(bool changeLength)
    {
        var path = Path.Combine(root, "edit.txt");
        File.WriteAllText(path, "old");
        var original = await client.GetFromJsonAsync<EditFileSystemItemResult>("/api/files/edit?path=" + Uri.EscapeDataString(path));
        Assert.NotNull(original);
        var external = changeLength ? "external content" : "new";
        File.WriteAllText(path, external);
        if (!changeLength)
        {
            File.SetLastWriteTimeUtc(path, original.LastWriteTime.UtcDateTime.AddMinutes(1));
        }
        using var response = await client.PostAsJsonAsync(
            "/api/files/edit",
            new EditFileSystemItemRequest(path, "replacement", original.Length, original.LastWriteTime, original.Encoding));
        await AssertProblem(response, HttpStatusCode.Conflict, "文件在编辑期间已被其他程序修改");
        Assert.Equal(external, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-edit-*.tmp"));
    }

    [Fact]
    public async Task ConcurrentEditsOfSameVersionAcceptOnlyOneSaveAndReturn409ForOther()
    {
        var path = Path.Combine(root, "concurrent-edit.txt");
        for (var round = 0; round < 20; round++)
        {
            File.WriteAllText(path, "original");
            var original = await client.GetFromJsonAsync<EditFileSystemItemResult>("/api/files/edit?path=" + Uri.EscapeDataString(path));
            Assert.NotNull(original);
            using var start = new Barrier(2);
            var content = new[] { new string('A', 1024 * 1024), new string('B', 1024 * 1024) };
            var saves = content.Select(value => Task.Run(async () =>
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
                return await client.PostAsJsonAsync(
                    "/api/files/edit",
                    new EditFileSystemItemRequest(path, value, original.Length, original.LastWriteTime, original.Encoding));
            })).ToArray();
            var responses = await Task.WhenAll(saves).WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                Assert.Single(responses.Where(response => response.StatusCode == HttpStatusCode.NoContent));
                var conflict = Assert.Single(responses.Where(response => response.StatusCode == HttpStatusCode.Conflict));
                await AssertProblem(conflict, HttpStatusCode.Conflict, "文件在编辑期间已被其他程序修改");
                var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.NoContent);
                Assert.Equal(content[winner], File.ReadAllText(path));
                Assert.Empty(Directory.GetFiles(root, ".winpilot-edit-*.tmp"));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
    }

    [Theory]
    [InlineData("path")]
    [InlineData("name")]
    [InlineData("binary")]
    public async Task FileValidationErrorsRemain400(string kind)
    {
        HttpResponseMessage response;
        string detail;
        if (kind == "path")
        {
            response = await client.GetAsync("/api/files/directory?path=relative");
            detail = "路径无效";
        }
        else if (kind == "name")
        {
            response = await client.PostAsJsonAsync("/api/files/file", new CreateFileSystemItemRequest(root, "CON"));
            detail = "名称不能使用 Windows 保留设备名";
        }
        else
        {
            var path = Path.Combine(root, "binary");
            File.WriteAllBytes(path, [65, 0, 66]);
            response = await client.GetAsync("/api/files/edit?path=" + Uri.EscapeDataString(path));
            detail = "此文件不支持编辑";
        }
        using (response)
        {
            await AssertProblem(response, HttpStatusCode.BadRequest, detail);
        }
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("文件操作失败", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(detail, json.RootElement.GetProperty("detail").GetString());
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
        Directory.Delete(root, true);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
