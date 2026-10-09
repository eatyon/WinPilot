using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Features.Files;

namespace WinPilot.Test;

public sealed class FileTransferAndEditTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinPilot.File.Test", Guid.NewGuid().ToString("N"));
    private readonly FileSystemService service = new(NullLogger<FileSystemService>.Instance);
    private readonly string downloads;

    public FileTransferAndEditTest()
    {
        downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
    }

    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    public void TextEditRoundTripsUnicodeAndLineBreaks(string name, bool bom)
    {
        var path = Path.Combine(root, "text.txt");
        var encoding = name == "utf-16" ? Encoding.Unicode : new UTF8Encoding(bom);
        File.WriteAllText(path, "中文\r\nfirst", encoding);
        var original = service.ReadForEdit(path);
        Assert.Equal("中文\r\nfirst", original.Content);
        Assert.Equal(name, original.Encoding);
        service.SaveEdit(new(path, "新内容\nsecond", original.Length, original.LastWriteTime, original.Encoding));
        var saved = service.ReadForEdit(path);
        Assert.Equal("新内容\nsecond", saved.Content);
        Assert.Equal(name, saved.Encoding);
        Assert.Equal(bom, File.ReadAllBytes(path).AsSpan().StartsWith(name == "utf-16" ? Encoding.Unicode.GetPreamble() : Encoding.UTF8.GetPreamble()));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-edit-*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExternalLengthOrTimestampChangePreventsOverwrite(bool changeLength)
    {
        var path = Path.Combine(root, "text.txt");
        File.WriteAllText(path, "old");
        var original = service.ReadForEdit(path);
        File.WriteAllText(path, changeLength ? "external-content" : "new");
        if (!changeLength) File.SetLastWriteTimeUtc(path, original.LastWriteTime.UtcDateTime.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => service.SaveEdit(new(path, "replacement", original.Length, original.LastWriteTime, original.Encoding)));
        Assert.Equal(changeLength ? "external-content" : "new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-edit-*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BinaryAndInvalidUtf8CannotBeEdited(bool nul)
    {
        var path = Path.Combine(root, "binary");
        File.WriteAllBytes(path, nul ? [65, 0, 66] : [0xff, 0xff, 0xff]);
        Assert.Throws<InvalidOperationException>(() => service.ReadForEdit(path));
    }

    [Fact]
    public void OversizedFileCannotBeEdited()
    {
        var path = Path.Combine(root, "large");
        using (var file = File.Create(path)) file.SetLength(11 * 1024 * 1024);
        Assert.Throws<InvalidOperationException>(() => service.ReadForEdit(path));
    }

    [Fact]
    public async Task UploadUsesOnlyFileNameAndDoesNotOverwriteExistingFile()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("upload"));
        var file = new FormFile(input, 0, input.Length, "file", "../notes.txt");
        await service.UploadAsync(root, file, CancellationToken.None);
        Assert.Equal("upload", File.ReadAllText(Path.Combine(root, "notes.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(root, file, CancellationToken.None));
        Assert.Equal("upload", File.ReadAllText(Path.Combine(root, "notes.txt")));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
    }

    [Fact]
    public async Task CancelledUploadLeavesNeitherTargetNorTemporaryFile()
    {
        using var input = new MemoryStream(new byte[1024]);
        var file = new FormFile(input, 0, input.Length, "file", "upload.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UploadAsync(root, file, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(root, "upload.bin")));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-upload-*.tmp"));
    }

    [Fact]
    public async Task SingleFileDownloadReturnsOriginalBytesAndReleasesFile()
    {
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "content");
        var artifact = await service.CreateDownloadAsync(new([path]), CancellationToken.None, downloads);
        using (artifact.Content)
        using (var reader = new StreamReader(artifact.Content))
            Assert.Equal("content", await reader.ReadToEndAsync());
        Assert.Equal("file.txt", artifact.FileName);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public async Task ArchiveIncludesNestedContentAndDeletesTemporaryFileOnDispose()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "中文.txt"), "content");
        var artifact = await service.CreateDownloadAsync(new([source]), CancellationToken.None, downloads);
        var temporaryPath = Assert.Single(Directory.GetFiles(downloads));
        using (artifact.Content)
        using (var zip = new ZipArchive(artifact.Content, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Contains(zip.Entries, entry => entry.FullName == "source/");
            Assert.Contains(zip.Entries, entry => entry.FullName == "source/nested/");
            var entry = Assert.Single(zip.Entries.Where(entry => !entry.FullName.EndsWith('/')));
            Assert.Equal("source/nested/中文.txt", entry.FullName);
            using var reader = new StreamReader(entry.Open());
            Assert.Equal("content", await reader.ReadToEndAsync());
        }
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task ArchiveNameConflictCleansTemporaryFile()
    {
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "same.txt"), "first");
        File.WriteAllText(Path.Combine(second, "same.txt"), "second");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDownloadAsync(
            new([Path.Combine(first, "same.txt"), Path.Combine(second, "same.txt")]), CancellationToken.None, downloads));
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public async Task CancelledArchiveCleansTemporaryFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateDownloadAsync(new([root]), cancellation.Token, downloads));
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public async Task EmptyDirectoryIsPreservedAndAsyncDisposeDeletesArchive()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
        var artifact = await service.CreateDownloadAsync(new([source]), CancellationToken.None, downloads);
        var temporaryPath = Assert.Single(Directory.GetFiles(downloads));
        using (var archive = new ZipArchive(artifact.Content, ZipArchiveMode.Read, leaveOpen: true))
            Assert.Equal("empty/", Assert.Single(archive.Entries).FullName);
        await artifact.Content.DisposeAsync();
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task FileAndDirectoryWithTheSameArchiveNameAreRejected()
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "same")).FullName;
        var parent = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        var file = Path.Combine(parent, "same");
        File.WriteAllText(file, "keep");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDownloadAsync(new([folder, file]), CancellationToken.None, downloads));
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public async Task CancelledSingleFileDownloadDoesNotOpenTheFile()
    {
        var path = Path.Combine(root, "file");
        File.WriteAllText(path, "keep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateDownloadAsync(new([path]), cancellation.Token, downloads));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void OversizedEditedContentDoesNotReplaceTheOriginal()
    {
        var path = Path.Combine(root, "small.txt");
        File.WriteAllText(path, "original");
        var original = service.ReadForEdit(path);
        Assert.Throws<InvalidOperationException>(() => service.SaveEdit(new(path, new string('x', 10 * 1024 * 1024 + 1), original.Length, original.LastWriteTime, original.Encoding)));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(root, ".winpilot-edit-*.tmp"));
    }

    [Fact]
    public void InvalidUtf8WithBomCannotBeEdited()
    {
        var path = Path.Combine(root, "invalid.txt");
        File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, 0xff]);
        Assert.Throws<InvalidOperationException>(() => service.ReadForEdit(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryLinksAreRejectedWithoutLeavingArchives(bool selectLink)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var link = Path.Combine(source, "loop");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, source }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDownloadAsync(new([selectLink ? link : source]), CancellationToken.None, downloads));
            Assert.Empty(Directory.GetFiles(downloads));
        }
        finally { Directory.Delete(link); }
    }

    public void Dispose() => Directory.Delete(root, true);
}
