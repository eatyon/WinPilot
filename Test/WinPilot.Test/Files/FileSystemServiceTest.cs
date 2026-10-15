using WinPilot.Features.Files;
using Microsoft.Extensions.Logging.Abstractions;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinPilot.Test;

public sealed class FileSystemServiceTest
{
    [Fact]
    public void LockedCopyPreservesNativeResultAndAbortFlagWithOriginalErrorMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Files.Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.txt");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(destination);
            File.WriteAllText(source, "locked content");
            using var lockedFile = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var native = new ShellFileOperation
            {
                Operation = 0x0002,
                Source = source + '\0' + '\0',
                Destination = destination + '\0' + '\0',
                Flags = 0x0004 | 0x0010 | 0x0400
            };
            var expectedCode = SHFileOperation(ref native);
            Assert.True(expectedCode != 0 || native.AnyOperationsAborted);
            var target = Path.Combine(destination, "source.txt");
            if (File.Exists(target)) File.Delete(target);
            var service = new FileSystemService(NullLogger<FileSystemService>.Instance);
            var failure = Assert.Throws<ShellFileOperationException>(() => service.Copy(new([source], destination)));
            Assert.Equal(expectedCode, failure.ReturnCode);
            Assert.Equal(native.AnyOperationsAborted, failure.AnyOperationsAborted);
            Assert.Equal(expectedCode != 0 ? $"无法复制：{new Win32Exception(expectedCode).Message}" : "复制操作已取消", failure.Message);
            Assert.IsAssignableFrom<IOException>(failure);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    [InlineData("delete")]
    public void ShellOperationsPreserveMissingSourcePrecheck(string operation)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Files.Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "missing.txt");
            var service = new FileSystemService(NullLogger<FileSystemService>.Instance);
            Assert.Throws<FileNotFoundException>(() =>
            {
                if (operation == "copy") service.Copy(new([source], root));
                else if (operation == "move") service.Move(new([source], root));
                else service.Delete(new([source]));
            });
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    [InlineData("delete")]
    public void ShellOperationsPreserveSuccessfulCopyMoveAndRecycle(string operation)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Files.Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.txt");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(destination);
            File.WriteAllText(source, "original content");
            var service = new FileSystemService(NullLogger<FileSystemService>.Instance);
            if (operation == "copy") service.Copy(new([source], destination));
            else if (operation == "move") service.Move(new([source], destination));
            else service.Delete(new([source]));
            Assert.Equal(operation == "copy", File.Exists(source));
            var target = Path.Combine(destination, "source.txt");
            Assert.Equal(operation != "delete", File.Exists(target));
            if (operation != "delete") Assert.Equal("original content", File.ReadAllText(target));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr Window;
        public uint Operation;
        public string Source;
        public string? Destination;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShellFileOperation operation);

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
