using WinPilot.Core;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;

namespace WinPilot.Test;

public sealed class ProgramTagServiceTest
{
    [Fact]
    public void Get_ReadsOnlyTheRequestedTag()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var service = new ProgramTagService(paths);
            service.Create(new("valid", "test.exe", "", "", false));
            File.WriteAllText(Path.Combine(paths.TagsDirectory, "broken.json"), "invalid json");
            Assert.Equal("valid", service.Get("valid").Name);
            Assert.Throws<FileNotFoundException>(() => service.Get("missing"));
        }
        finally { if (Directory.Exists(applicationDirectory)) Directory.Delete(applicationDirectory, true); }
    }

    [Fact]
    public void ConcurrentCreates_DoNotOverwriteAnExistingTagOrLeaveTemporaryFiles()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var service = new ProgramTagService(paths);
            var created = 0;
            Parallel.For(0, 20, index =>
            {
                try
                {
                    service.Create(new("same", $"{index}.exe", "", "", false));
                    Interlocked.Increment(ref created);
                }
                catch (InvalidOperationException) { }
            });
            Assert.Equal(1, created);
            Assert.Single(service.GetAll());
            Assert.Empty(Directory.EnumerateFiles(paths.TagsDirectory, "*.tmp"));
        }
        finally { if (Directory.Exists(applicationDirectory)) Directory.Delete(applicationDirectory, true); }
    }

    [Fact]
    public void FailedRename_PreservesTheOriginalTag()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var service = new ProgramTagService(paths);
            var original = new ProgramTag("original", "old.exe", "", "", false);
            service.Create(original);
            Directory.CreateDirectory(Path.Combine(paths.TagsDirectory, "blocked.json"));
            Assert.ThrowsAny<IOException>(() => service.Update("original", original with { Name = "blocked", ProgramPath = "new.exe" }));
            Assert.Equal(original, service.Get("original"));
            Assert.Empty(Directory.EnumerateFiles(paths.TagsDirectory, "*.tmp"));
        }
        finally { if (Directory.Exists(applicationDirectory)) Directory.Delete(applicationDirectory, true); }
    }

    [Fact]
    public void FailedReplacement_CleansTemporaryFilesAndPreservesTheOriginalTag()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var service = new ProgramTagService(paths);
            var original = new ProgramTag("original", "old.exe", "", "", false);
            service.Create(original);
            using (File.Open(Path.Combine(paths.TagsDirectory, "original.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(() => service.Update("original", original with { ProgramPath = "new.exe" }));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }
            Assert.Equal(original, service.Get("original"));
            Assert.Empty(Directory.EnumerateFiles(paths.TagsDirectory, "*.tmp"));
        }
        finally { if (Directory.Exists(applicationDirectory)) Directory.Delete(applicationDirectory, true); }
    }

    [Fact]
    public void CreateAndUpdate_UsesSeparateTagFilesAndRenamesWithTheTag()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var service = new ProgramTagService(paths);
            Assert.False(Directory.Exists(paths.TagsDirectory));

            service.Create(new ProgramTag("测试", "C:\\Tools\\test.exe", "--safe", "C:\\Tools", false));
            service.Update("测试", new ProgramTag("测试二", "C:\\Tools\\test.exe", "", "", true));

            Assert.False(File.Exists(Path.Combine(paths.TagsDirectory, "测试.json")));
            var tagFile = Path.Combine(paths.TagsDirectory, "测试二.json");
            Assert.True(File.Exists(tagFile));
            var tag = Assert.Single(service.GetAll());
            Assert.Equal("测试二", tag.Name);
            Assert.True(tag.RunElevated);
        }
        finally
        {
            if (Directory.Exists(applicationDirectory)) Directory.Delete(applicationDirectory, true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("a/b")]
    public void Create_RejectsInvalidTagName(string name)
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ProgramTagService(PortableDataPaths.Create(applicationDirectory));
            Assert.Throws<InvalidOperationException>(() => service.Create(new ProgramTag(name, "C:\\Tools\\test.exe", "", "", false)));
        }
        finally
        {
            if (Directory.Exists(applicationDirectory)) Directory.Delete(applicationDirectory, true);
        }
    }
}
