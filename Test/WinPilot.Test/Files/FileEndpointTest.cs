using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
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

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
        Directory.Delete(root, true);
    }

    private sealed record UploadResult(string FileName, bool Success, string? Error);
}
