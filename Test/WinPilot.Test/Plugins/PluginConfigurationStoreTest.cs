using System.Text.Json.Nodes;
using WinPilot.Features.Plugins;

namespace WinPilot.Test;

public sealed class PluginConfigurationStoreTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winpilot-plugin-config", Guid.NewGuid().ToString("N"));

    private readonly PluginConfigurationStore configurationStore = new();

    [Fact]
    public void LockedTemporaryFileReportsSaveAndCleanupFailuresWithoutChangingConfiguration()
    {
        var path = Path.Combine(root, "config.json");
        configurationStore.Update(path, _ => new JsonObject { ["value"] = 7 });
        var original = File.ReadAllText(path);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, "temporary");
        using (var locked = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<AggregateException>(() => configurationStore.Update(path, _ => new JsonObject { ["value"] = 8 }));
            Assert.Equal(2, error.InnerExceptions.Count);
            Assert.All(error.InnerExceptions, exception => Assert.IsAssignableFrom<IOException>(exception));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal("temporary", File.ReadAllText(temporary));
        }
        File.Delete(temporary);
        configurationStore.Update(path, _ => new JsonObject { ["value"] = 8 });
        Assert.False(File.Exists(temporary));
        Assert.Equal(8, configurationStore.Read(path)!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task UpdatesReadLatestContentAndLeaveNoTemporaryFile()
    {
        var path = Path.Combine(root, "config.json");
        Assert.Null(configurationStore.Read(path));
        Assert.False(Directory.Exists(root));
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            configurationStore.Update(path, current =>
            {
                current["count"] = (current["count"]?.GetValue<int>() ?? 0) + 1;
                return current;
            }))));
        Assert.Equal(100, configurationStore.Read(path)!["count"]!.GetValue<int>());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    public void InvalidExistingConfigurationIsNeverOverwritten(string content)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.json");
        File.WriteAllText(path, content);
        Assert.Throws<InvalidDataException>(() => configurationStore.Update(path, _ => new JsonObject()));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void FailedUpdateLeavesOriginalFileIntact()
    {
        var path = Path.Combine(root, "config.json");
        configurationStore.Update(path, _ => new JsonObject { ["value"] = 7 });
        var original = File.ReadAllText(path);
        Assert.Throws<InvalidOperationException>(() => configurationStore.Update(path, _ => throw new InvalidOperationException()));
        Assert.Equal(original, File.ReadAllText(path));
        Directory.CreateDirectory(path + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => configurationStore.Update(path, _ => new JsonObject()));
        Assert.Equal(original, File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
