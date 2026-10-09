using WinPilot.Features.Files;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinPilot.Test;

public sealed class FileSystemServiceTest
{
    [Fact]
    public void CreateRenameAndBrowse_UsesAbsolutePathsAndReportsEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Files.Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FileSystemService(NullLogger<FileSystemService>.Instance);
            service.CreateDirectory(new(root, "资料"));
            service.CreateFile(new(root, "notes.txt"));
            service.Rename(new(Path.Combine(root, "notes.txt"), "renamed.txt"));

            var result = service.GetDirectory(root);

            Assert.Equal(Path.GetFullPath(root), result.Path);
            Assert.Contains(result.Entries, entry => entry.Name == "资料" && entry.IsDirectory && entry.Size is null);
            Assert.Contains(result.Entries, entry => entry.Name == "renamed.txt" && !entry.IsDirectory && entry.Type == "文件");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Copy_RejectsCopyingDirectoryIntoItsOwnChild()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Files.Test", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var child = Path.Combine(source, "child");
        Directory.CreateDirectory(child);
        try
        {
            var service = new FileSystemService(NullLogger<FileSystemService>.Instance);

            var exception = Assert.Throws<InvalidOperationException>(() => service.Copy(new([source], child)));

            Assert.Contains("自身或其子目录", exception.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("CON")]
    [InlineData("a/b")]
    public void CreateFile_RejectsUnsafeNames(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Files.Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FileSystemService(NullLogger<FileSystemService>.Instance);
            Assert.Throws<InvalidOperationException>(() => service.CreateFile(new(root, name)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
