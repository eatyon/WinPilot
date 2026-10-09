using System.IO.Compression;
using System.Security.Cryptography;
using WinPilot.Updater;

namespace WinPilot.Test;

public sealed class UpdatePackageTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinPilot.Update.Test", Guid.NewGuid().ToString("N"));

    public UpdatePackageTest() => Directory.CreateDirectory(root);

    [Fact]
    public async Task HashMismatchDoesNotExtract()
    {
        var (path, _) = Package(["WinPilot/WinPilot.exe", "WinPilot/WinPilot.Updater.exe", "WinPilot/wwwroot/index.html"]);
        var staging = Path.Combine(root, "staging");
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdatePackage.ExtractVerifiedAsync(path, new string('0', 64), staging));
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("WinPilot/../../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    public async Task TraversalEntryCannotWriteOutsideStaging(string entry)
    {
        var (path, hash) = Package([entry]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdatePackage.ExtractVerifiedAsync(path, hash, Path.Combine(root, "staging")));
        Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
    }

    [Fact]
    public async Task AbsoluteEntryCannotWriteOutsideStaging()
    {
        var escaped = Path.Combine(root, "escaped.txt");
        var (path, hash) = Package([escaped]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdatePackage.ExtractVerifiedAsync(path, hash, Path.Combine(root, "staging")));
        Assert.False(File.Exists(escaped));
    }

    [Fact]
    public async Task CorruptArchiveIsRejectedEvenWithMatchingHash()
    {
        var path = Path.Combine(root, "package.zip");
        File.WriteAllText(path, "not a zip archive");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackage.ExtractVerifiedAsync(path, hash, Path.Combine(root, "staging")));
    }

    [Theory]
    [InlineData("WinPilot/WinPilot.exe")]
    [InlineData("WinPilot/WinPilot.Updater.exe")]
    [InlineData("WinPilot/wwwroot/index.html")]
    public async Task MissingRequiredComponentRejectsPackage(string missing)
    {
        var entries = new[] { "WinPilot/WinPilot.exe", "WinPilot/WinPilot.Updater.exe", "WinPilot/wwwroot/index.html", "WinPilot/wwwroot/assets/app.js" };
        var (path, hash) = Package(entries.Where(entry => entry != missing));
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdatePackage.ExtractVerifiedAsync(path, hash, Path.Combine(root, "staging")));
    }

    [Fact]
    public async Task VerifiedPackageReplacesExecutablesAndWebWhilePreservingUserFiles()
    {
        var (path, hash) = Package(["WinPilot/WinPilot.exe", "WinPilot/WinPilot.Updater.exe", "WinPilot/wwwroot/index.html", "WinPilot/wwwroot/assets/app.js"]);
        var packageRoot = await UpdatePackage.ExtractVerifiedAsync(path, hash.ToLowerInvariant(), Path.Combine(root, "staging"));
        var target = Path.Combine(root, "installed");
        Directory.CreateDirectory(Path.Combine(target, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(target, "User", "Plugin", "sample"));
        File.WriteAllText(Path.Combine(target, "WinPilot.exe"), "old");
        File.WriteAllText(Path.Combine(target, "WinPilot.Updater.exe"), "old");
        File.WriteAllText(Path.Combine(target, "wwwroot", "obsolete.js"), "old");
        File.WriteAllText(Path.Combine(target, "User", "config.json"), "user-config");
        File.WriteAllText(Path.Combine(target, "User", "Plugin", "sample", "config.json"), "plugin-config");
        UpdatePackage.ReplaceFiles(packageRoot, target);
        Assert.Equal("WinPilot/WinPilot.exe", File.ReadAllText(Path.Combine(target, "WinPilot.exe")));
        Assert.Equal("WinPilot/WinPilot.Updater.exe", File.ReadAllText(Path.Combine(target, "WinPilot.Updater.exe")));
        Assert.Equal("WinPilot/wwwroot/assets/app.js", File.ReadAllText(Path.Combine(target, "wwwroot", "assets", "app.js")));
        Assert.False(File.Exists(Path.Combine(target, "wwwroot", "obsolete.js")));
        Assert.Equal("user-config", File.ReadAllText(Path.Combine(target, "User", "config.json")));
        Assert.Equal("plugin-config", File.ReadAllText(Path.Combine(target, "User", "Plugin", "sample", "config.json")));
    }

    [Fact]
    public async Task FailedReplacementRestoresEarlierFilesAndLeavesUserDataUntouched()
    {
        var (path, hash) = Package(["WinPilot/WinPilot.exe", "WinPilot/WinPilot.Updater.exe", "WinPilot/wwwroot/index.html"]);
        var packageRoot = await UpdatePackage.ExtractVerifiedAsync(path, hash, Path.Combine(root, "staging"));
        var target = Path.Combine(root, "installed");
        Directory.CreateDirectory(Path.Combine(target, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(target, "User"));
        File.WriteAllText(Path.Combine(target, "WinPilot.exe"), "old-main");
        File.WriteAllText(Path.Combine(target, "WinPilot.Updater.exe"), "old-updater");
        File.WriteAllText(Path.Combine(target, "wwwroot", "index.html"), "old-web");
        File.WriteAllText(Path.Combine(target, "User", "config.json"), "user-config");
        using (var locked = new FileStream(Path.Combine(target, "WinPilot.Updater.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => UpdatePackage.ReplaceFiles(packageRoot, target));
        Assert.Equal("old-main", File.ReadAllText(Path.Combine(target, "WinPilot.exe")));
        Assert.Equal("old-updater", File.ReadAllText(Path.Combine(target, "WinPilot.Updater.exe")));
        Assert.Equal("old-web", File.ReadAllText(Path.Combine(target, "wwwroot", "index.html")));
        Assert.Equal("user-config", File.ReadAllText(Path.Combine(target, "User", "config.json")));
        Assert.Empty(Directory.EnumerateDirectories(target, ".winpilot-update-*"));
    }

    [Fact]
    public void CleanupIsLimitedToGeneratedTemporaryDirectoriesOutsideTheInstallation()
    {
        var valid = Path.Combine(Path.GetTempPath(), "WinPilot-update-" + Guid.NewGuid().ToString("N"));
        UpdatePackage.ValidateCleanupDirectory(valid, root);
        Assert.Throws<InvalidOperationException>(() => UpdatePackage.ValidateCleanupDirectory(root, root));
        Assert.Throws<InvalidOperationException>(() => UpdatePackage.ValidateCleanupDirectory(Path.GetTempPath(), root));
        Assert.Throws<InvalidOperationException>(() => UpdatePackage.ValidateCleanupDirectory(Path.Combine(Path.GetTempPath(), "WinPilot-update-invalid"), root));
        Assert.Throws<InvalidOperationException>(() => UpdatePackage.ValidateCleanupDirectory(valid, Path.Combine(valid, "installed")));
    }

    private (string Path, string Hash) Package(IEnumerable<string> names)
    {
        var path = Path.Combine(root, "package.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        foreach (var name in names)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(name);
        }
        return (path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    public void Dispose() => Directory.Delete(root, true);
}
