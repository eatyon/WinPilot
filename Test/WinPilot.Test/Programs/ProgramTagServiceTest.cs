using WinPilot.Core;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WinPilot.Test;

public sealed class ProgramTagServiceTest
{
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Name\":\"broken\",\"ProgramPath\":\"\"}")]
    public void GetAllIsolatesDamagedFilesAndReportsOriginalErrorWithoutChangingFiles(string damaged)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var logger = new TagLogger();
            using var services = new ServiceCollection().AddSingleton<ILogger<ProgramTagService>>(logger).BuildServiceProvider();
            var service = ActivatorUtilities.CreateInstance<ProgramTagService>(services, paths);
            service.Create(new("healthy", "app.exe", "", "", false));
            var file = Path.Combine(paths.TagsDirectory, "broken.json");
            File.WriteAllText(file, damaged);
            Assert.Equal("healthy", Assert.Single(service.GetAll()).Name);
            var error = Assert.Single(logger.Errors);
            Assert.Contains("broken.json", error.Message);
            Assert.IsType<InvalidDataException>(error.Exception);
            Assert.Equal(damaged, File.ReadAllText(file));
            Assert.Throws<InvalidDataException>(() => service.Get("broken"));
            Assert.Equal(2, Directory.GetFiles(paths.TagsDirectory).Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void GetAllSkipsTemporarilyUnreadableFileWithoutDiscardingOtherTags()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var logger = new TagLogger();
            using var services = new ServiceCollection().AddSingleton<ILogger<ProgramTagService>>(logger).BuildServiceProvider();
            var service = ActivatorUtilities.CreateInstance<ProgramTagService>(services, paths);
            service.Create(new("healthy", "app.exe", "", "", false));
            service.Create(new("locked", "locked.exe", "", "", false));
            var file = Path.Combine(paths.TagsDirectory, "locked.json");
            var original = File.ReadAllText(file);
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal("healthy", Assert.Single(service.GetAll()).Name);
                var error = Assert.Single(logger.Errors);
                Assert.Contains("locked.json", error.Message);
                Assert.IsAssignableFrom<IOException>(error.Exception);
            }
            Assert.Equal(2, service.GetAll().Count);
            Assert.Equal(original, File.ReadAllText(file));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, "healthy")]
    [InlineData(true, "healthy")]
    [InlineData(false, "other")]
    [InlineData(true, "other")]
    public void MismatchedFileIdentityCannotIntroduceOrOverwriteTagNames(bool writeAliasFirst, string aliasName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var service = new ProgramTagService(paths);
            Directory.CreateDirectory(paths.TagsDirectory);
            var file = Path.Combine(paths.TagsDirectory, "alias.json");
            var alias = JsonSerializer.Serialize(new ProgramTag(aliasName, "alias.exe", "", "", false));
            if (writeAliasFirst) File.WriteAllText(file, alias);
            service.Create(new("healthy", "app.exe", "", "", false));
            if (!writeAliasFirst) File.WriteAllText(file, alias);
            var healthy = Assert.Single(service.GetAll());
            Assert.Equal("healthy", healthy.Name);
            Assert.Equal("app.exe", healthy.ProgramPath);
            Assert.Throws<InvalidDataException>(() => service.Get("alias"));
            Assert.Equal(alias, File.ReadAllText(file));
            Assert.Equal(2, Directory.GetFiles(paths.TagsDirectory).Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CaseOnlyRenameStillMatchesWindowsFileIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ProgramTagService(PortableDataPaths.Create(directory));
            var tag = new ProgramTag("Name", "app.exe", "", "", false);
            service.Create(tag);
            service.Update("Name", tag with { Name = "NAME" });
            Assert.Equal("NAME", service.Get("name").Name);
            Assert.Equal("NAME", Assert.Single(service.GetAll()).Name);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class TagLogger : ILogger<ProgramTagService>
    {
        internal List<(Exception Exception, string Message)> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Error && exception is not null) Errors.Add((exception, formatter(state, exception)));
        }
    }

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
            Assert.Contains("测试二", File.ReadAllText(tagFile));
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
