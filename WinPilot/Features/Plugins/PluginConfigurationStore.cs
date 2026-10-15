using System.Collections.Concurrent;
using WinPilot.Core;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinPilot.Features.Plugins;

internal sealed class PluginConfigurationStore
{
    private readonly ConcurrentDictionary<string, object> gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public JsonObject? Read(string path)
    {
        path = Path.GetFullPath(path);
        lock (gates.GetOrAdd(Path.GetDirectoryName(path)!, _ => new object()))
            return ReadCore(path);
    }

    public void Update(string path, Func<JsonObject, JsonObject> update)
    {
        path = Path.GetFullPath(path);
        lock (gates.GetOrAdd(Path.GetDirectoryName(path)!, _ => new object()))
        {
            WriteCore(path, update(ReadCore(path) ?? new JsonObject()));
        }
    }

    public void Initialize(string path, Func<JsonObject> createDefaults)
    {
        path = Path.GetFullPath(path);
        lock (gates.GetOrAdd(Path.GetDirectoryName(path)!, _ => new object()))
            if (!File.Exists(path)) WriteCore(path, createDefaults());
    }

    private static void WriteCore(string path, JsonObject configuration)
    {
        var content = configuration.ToJsonString(JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryFile = path + ".tmp";
        FileOperationRecovery.WithCleanup(() =>
        {
            File.WriteAllText(temporaryFile, content);
            File.Move(temporaryFile, path, overwrite: true);
        }, () => { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); });
    }

    private static JsonObject? ReadCore(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("插件配置文件根节点必须是 JSON 对象");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("插件配置文件不是有效 JSON", exception);
        }
    }
}
