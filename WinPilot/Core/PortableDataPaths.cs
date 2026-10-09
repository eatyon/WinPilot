namespace WinPilot.Core;

public sealed class PortableDataPaths
{
    private PortableDataPaths(string applicationDirectory)
    {
        ApplicationDirectory = applicationDirectory;
        DataDirectory = Path.Combine(applicationDirectory, "User");
        LogsDirectory = Path.Combine(applicationDirectory, "logs");
        TasksDirectory = Path.Combine(DataDirectory, "Task");
        TagsDirectory = Path.Combine(DataDirectory, "Tag");
        PluginsDirectory = Path.Combine(DataDirectory, "Plugin");
        PluginDataDirectory = Path.Combine(DataDirectory, "PluginData");
        WebDirectory = Path.Combine(DataDirectory, "Web");
        ConfigurationFile = Path.Combine(DataDirectory, "config.json");
        AuthenticationFile = Path.Combine(DataDirectory, "auth.json");
    }

    public string ApplicationDirectory { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string TasksDirectory { get; }
    public string TagsDirectory { get; }
    public string PluginsDirectory { get; }
    public string PluginDataDirectory { get; }
    public string WebDirectory { get; }
    public string WebIndexFile => Path.Combine(WebDirectory, "index.html");
    public string ConfigurationFile { get; }
    public string AuthenticationFile { get; }

    public static PortableDataPaths Create(string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            throw new ArgumentException("程序目录不能为空", nameof(applicationDirectory));
        }

        var paths = new PortableDataPaths(Path.GetFullPath(applicationDirectory));
        return paths;
    }

    public void EnsurePluginsDirectory() => Directory.CreateDirectory(PluginsDirectory);
}
