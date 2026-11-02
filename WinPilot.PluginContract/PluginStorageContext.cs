using System.Text.Json;

namespace WinPilot.PluginContract;

/// <summary>
/// 从插件数据目录读取配置和数据，仅接受不含目录分隔符的单级文件名。
/// </summary>
public sealed class PluginStorageContext(string dataDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public string DataDirectory { get; } = dataDirectory;

    public string? ReadConfig() => ReadText("config.json");

    public T? ReadConfig<T>() => JsonSerializer.Deserialize<T>(ReadConfig() ?? "{}", JsonOptions);

    public T? ReadData<T>(string fileName) => JsonSerializer.Deserialize<T>(ReadText(fileName) ?? "null", JsonOptions);

    private string? ReadText(string fileName)
    {
        if (Path.IsPathRooted(fileName)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName is "." or "..")
        {
            throw new ArgumentException("插件数据文件必须是单级文件名", nameof(fileName));
        }
        var path = Path.Combine(DataDirectory, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
