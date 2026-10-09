using System.IO.Compression;
using System.Security.Cryptography;

namespace WinPilot.Updater;

internal static class UpdatePackage
{
    internal static void ValidateCleanupDirectory(string directory, string targetDirectory)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(directory);
        if (!string.Equals(Path.GetDirectoryName(directory), temp, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith("WinPilot-update-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["WinPilot-update-".Length..], "N", out _))
            throw new InvalidOperationException("更新临时目录无效");
        var target = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(directory, target, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新临时目录不能与程序目录重叠");
        foreach (var path in new[] { directory, Path.Combine(directory, "staging") })
            if ((Directory.Exists(path) || File.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("更新临时目录不能是符号链接或目录联接");
    }

    internal static async Task<string> ExtractVerifiedAsync(string packagePath, string packageHash, string stagingDirectory)
    {
        await using (var input = File.OpenRead(packagePath))
        {
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input)), packageHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新包校验失败");
        }
        stagingDirectory = Path.GetFullPath(stagingDirectory);
        await using (var archive = await ZipFile.OpenReadAsync(packagePath))
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
            if (!destination.StartsWith(stagingDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新包包含无效路径");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await entry.ExtractToFileAsync(destination, true);
        }
        var packageRoot = Path.Combine(stagingDirectory, "WinPilot");
        if (!File.Exists(Path.Combine(packageRoot, "WinPilot.exe"))
            || !File.Exists(Path.Combine(packageRoot, "WinPilot.Updater.exe"))
            || !File.Exists(Path.Combine(packageRoot, "wwwroot", "index.html")))
            throw new InvalidOperationException("更新包内容不完整");
        return packageRoot;
    }

    internal static void ReplaceFiles(string packageRoot, string targetDirectory)
    {
        targetDirectory = Path.GetFullPath(targetDirectory);
        if (Directory.Exists(targetDirectory) && File.GetAttributes(targetDirectory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("更新目标不能是符号链接或目录联接");
        var names = new[] { "WinPilot.exe", "WinPilot.Updater.exe", "wwwroot" };
        foreach (var name in names)
        {
            var path = Path.Combine(targetDirectory, name);
            if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("更新目标不能是符号链接或目录联接");
        }
        var transaction = Path.Combine(targetDirectory, ".winpilot-update-" + Guid.NewGuid().ToString("N"));
        var incoming = Path.Combine(transaction, "incoming");
        var backup = Path.Combine(transaction, "backup");
        var movedOld = new List<string>();
        var installed = new List<string>();
        var canCleanup = true;
        try
        {
            Directory.CreateDirectory(incoming);
            Directory.CreateDirectory(backup);
            foreach (var name in names)
                MoveOrCopy(Path.Combine(packageRoot, name), Path.Combine(incoming, name), copy: true);
            try
            {
                foreach (var name in names)
                {
                    var destination = Path.Combine(targetDirectory, name);
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        MoveOrCopy(destination, Path.Combine(backup, name), copy: false);
                        movedOld.Add(name);
                    }
                    MoveOrCopy(Path.Combine(incoming, name), destination, copy: false);
                    installed.Add(name);
                }
            }
            catch (Exception installError)
            {
                var errors = new List<Exception> { installError };
                foreach (var name in names.Reverse())
                {
                    try
                    {
                        var destination = Path.Combine(targetDirectory, name);
                        if (installed.Contains(name))
                        {
                            if (Directory.Exists(destination)) Directory.Delete(destination, true);
                            else File.Delete(destination);
                        }
                        if (movedOld.Contains(name)) MoveOrCopy(Path.Combine(backup, name), destination, copy: false);
                    }
                    catch (Exception restoreError) { errors.Add(restoreError); }
                }
                if (errors.Count > 1)
                {
                    canCleanup = false;
                    throw new AggregateException($"更新失败且未能完整恢复，原文件保留在 {backup}", errors);
                }
                throw;
            }
        }
        finally
        {
            if (canCleanup && Directory.Exists(transaction))
            {
                try { Directory.Delete(transaction, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void MoveOrCopy(string source, string destination, bool copy)
    {
        if (Directory.Exists(source))
        {
            if (copy) CopyDirectory(source, destination);
            else Directory.Move(source, destination);
        }
        else if (copy) File.Copy(source, destination);
        else File.Move(source, destination);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
