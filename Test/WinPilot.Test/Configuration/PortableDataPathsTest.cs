using WinPilot.Core;

namespace WinPilot.Test;

public sealed class PortableDataPathsTest
{
    [Fact]
    public void InitializationCreatesConfigurationAtPortablePathWithoutOtherDataDirectories()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);

            Assert.False(Directory.Exists(paths.LogsDirectory));
            Assert.False(Directory.Exists(paths.TasksDirectory));
            Assert.False(File.Exists(paths.ConfigurationFile));

            WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile);
            Assert.True(File.Exists(paths.ConfigurationFile));
            Assert.True(Directory.Exists(paths.DataDirectory));
            Assert.EndsWith("User", paths.DataDirectory, StringComparison.Ordinal);
            Assert.EndsWith(Path.Combine("User", "config.json"), paths.ConfigurationFile, StringComparison.Ordinal);
            Assert.False(Directory.Exists(paths.TasksDirectory));
            Assert.False(Directory.Exists(paths.LogsDirectory));
        }
        finally
        {
            if (Directory.Exists(applicationDirectory))
            {
                Directory.Delete(applicationDirectory, recursive: true);
            }
        }
    }

}
