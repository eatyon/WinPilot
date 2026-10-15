using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
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
        using var saved = await client.PostAsJsonAsync("/api/files/edit", new EditFileSystemItemRequest(path, "saved", original!.Length, original.LastWriteTime, original.Encoding));
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
    public async Task UploadReturnsPerFileResultsAndNeverOverwrites()
    {
        var path = Path.Combine(root, "exists.txt");
        File.WriteAllText(path, "keep");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(root), "directory");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("replace")), "files", "exists.txt");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("new")), "files", "new.txt");
        using var response = await client.PostAsync("/api/files/upload", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<UploadResult[]>();
        Assert.False(results![0].Success);
        Assert.True(results[1].Success);
        Assert.Equal("keep", File.ReadAllText(path));
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, "new.txt")));
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
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/files/{operation}" +
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
        if (!changeLength) File.SetLastWriteTimeUtc(path, original.LastWriteTime.UtcDateTime.AddMinutes(1));
        using var response = await client.PostAsJsonAsync("/api/files/edit", new EditFileSystemItemRequest(path, "replacement", original.Length, original.LastWriteTime, original.Encoding));
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
                return await client.PostAsJsonAsync("/api/files/edit", new EditFileSystemItemRequest(path, value, original.Length, original.LastWriteTime, original.Encoding));
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
            finally { foreach (var response in responses) response.Dispose(); }
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
        using (response) await AssertProblem(response, HttpStatusCode.BadRequest, detail);
    }

    [Theory]
    [InlineData("io", "文件操作失败")]
    [InlineData("permission", "无法访问此项目，没有足够的权限")]
    [InlineData("sharing", "文件正在被其他程序使用，请稍后重试")]
    [InlineData("missing", "文件或文件夹不存在")]
    public async Task UploadReadFailureRemainsPerFileAndLaterFileSucceeds(string kind, string message)
    {
        var secret = Path.Combine(root, "private-diagnostics.txt");
        Exception failure = kind switch
        {
            "permission" => new UnauthorizedAccessException(secret),
            "sharing" => new IOException(secret, unchecked((int)0x80070020)),
            "missing" => new FileNotFoundException(secret),
            _ => new IOException(secret)
        };
        using var broken = new FailingReadStream(failure);
        using var good = new MemoryStream(Encoding.UTF8.GetBytes("saved"));
        var files = new FormFileCollection
        {
            new FormFile(broken, 0, broken.Length, "files", "broken.txt"),
            new FormFile(good, 0, good.Length, "files", "good.txt")
        };
        var form = new FormCollection(new Dictionary<string, StringValues> { ["directory"] = root }, files);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<FileSystemService>();
        await using var uploadApp = builder.Build();
        uploadApp.Use(async (context, next) => { context.Features.Set<IFormFeature>(new SuppliedFormFeature(form)); await next(context); });
        uploadApp.MapFileEndpoints();
        await uploadApp.StartAsync();
        using var uploadClient = uploadApp.GetTestClient();
        using var response = await uploadClient.PostAsync("/api/files/upload", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var results = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(new[] { "error", "fileName", "success" }, results[0].EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("broken.txt", results[0].GetProperty("fileName").GetString());
        Assert.False(results[0].GetProperty("success").GetBoolean());
        Assert.Equal(message, results[0].GetProperty("error").GetString());
        Assert.True(results[1].GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, results[1].GetProperty("error").ValueKind);
        Assert.Equal("saved", File.ReadAllText(Path.Combine(root, "good.txt")));
        Assert.False(File.Exists(Path.Combine(root, "broken.txt")));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
    }

    [Fact]
    public async Task UploadParserBodyLimitExceptionReturns413()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<FileSystemService>();
        await using var parserApp = builder.Build();
        using var body = new FailingReadStream(new BadHttpRequestException("Request body too large", 413));
        parserApp.Use(async (context, next) => { context.Request.Body = body; await next(context); });
        parserApp.MapFileEndpoints();
        await parserApp.StartAsync();
        using var parserClient = parserApp.GetTestClient();
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(root), "directory");
        using var response = await parserClient.PostAsync("/api/files/upload", content);
        await AssertProblem(response, HttpStatusCode.RequestEntityTooLarge, "上传请求体超过大小限制");
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task KestrelUploadHonorsBodyLimitWithoutChangingSuccessfulMultiFileResults(bool unknownLength, int limitOffset)
    {
        var first = Encoding.UTF8.GetBytes("中文内容");
        var second = new byte[1024];
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(root), "directory");
        form.Add(new ByteArrayContent(first), "files", "first.txt");
        form.Add(new ByteArrayContent(second), "files", "second.txt");
        var bytes = await form.ReadAsByteArrayAsync();
        var chunkFramingLength = unknownLength ? Encoding.ASCII.GetByteCount($"{bytes.Length:X}\r\n\r\n0\r\n\r\n") : 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = bytes.Length + chunkFramingLength + limitOffset;
        });
        builder.Services.AddSingleton<FileSystemService>();
        await using var uploadApp = builder.Build();
        long? receivedLength = null;
        var receivedChunked = false;
        uploadApp.Use(async (context, next) =>
        {
            receivedLength = context.Request.ContentLength;
            receivedChunked = context.Request.Headers.TransferEncoding.ToString().Contains("chunked", StringComparison.OrdinalIgnoreCase);
            await next(context);
        });
        uploadApp.MapFileEndpoints();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await uploadApp.StartAsync(timeout.Token);
        try
        {
            var address = uploadApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var uploadClient = new HttpClient { BaseAddress = new Uri(address) };
            using HttpContent content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            content.Headers.ContentType = form.Headers.ContentType;
            using var response = await uploadClient.PostAsync("/api/files/upload", content, timeout.Token);
            Assert.Equal(unknownLength ? null : (long?)bytes.Length, receivedLength);
            Assert.Equal(unknownLength, receivedChunked);
            if (limitOffset < 0)
            {
                await AssertProblem(response, HttpStatusCode.RequestEntityTooLarge, "上传请求体超过大小限制");
                Assert.Empty(Directory.GetFileSystemEntries(root));
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                var results = await response.Content.ReadFromJsonAsync<UploadResult[]>(timeout.Token);
                Assert.Collection(results!,
                    result => Assert.Equal(new UploadResult("first.txt", true, null), result),
                    result => Assert.Equal(new UploadResult("second.txt", true, null), result));
                Assert.Equal(first, File.ReadAllBytes(Path.Combine(root, "first.txt")));
                Assert.Equal(second, File.ReadAllBytes(Path.Combine(root, "second.txt")));
                Assert.Equal(2, Directory.GetFiles(root).Length);
            }
            Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
        }
        finally { await uploadApp.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task UploadInvalidMultipartHeaderStillReturns400WithoutWritingFiles()
    {
        using var content = new StringContent("--probe\r\nbroken-header\r\n\r\nx\r\n--probe--\r\n", Encoding.UTF8);
        content.Headers.ContentType = new("multipart/form-data") { Parameters = { new("boundary", "probe") } };
        using var response = await client.PostAsync("/api/files/upload", content);
        await AssertProblem(response, HttpStatusCode.BadRequest, "上传表单格式无效");
        Assert.Empty(Directory.GetFileSystemEntries(root));
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

    private sealed class SuppliedFormFeature(IFormCollection form) : IFormFeature
    {
        public bool HasFormContentType => true;
        public IFormCollection? Form { get; set; } = form;
        public IFormCollection ReadForm() => Form!;
        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken) => Task.FromResult(Form!);
    }

    private sealed class FailingReadStream(Exception failure) : MemoryStream(new byte[] { 1, 2, 3 })
    {
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override int Read(Span<byte> buffer) => throw failure;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(failure);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(failure);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
        Directory.Delete(root, true);
    }

    private sealed record UploadResult(string FileName, bool Success, string? Error);
}
