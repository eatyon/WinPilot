using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinPilot.Web;

public static class PluginConfigurationResponse
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static (string Definition, string Storage) MaskSensitiveValues(string definitionJson, string storageJson)
    {
        var definition = JsonNode.Parse(definitionJson) as JsonArray
            ?? throw new InvalidOperationException("settings.json 必须是数组");
        var storage = JsonNode.Parse(storageJson) as JsonObject
            ?? throw new InvalidOperationException("插件配置格式无效");
        foreach (var setting in definition.OfType<JsonObject>())
        {
            var name = setting["name"]!.GetValue<string>();
            if (setting["type"]?.GetValue<string>() != "collection")
                MaskField(setting, [storage]);
            else if (setting["mode"]?.GetValue<string>() == "fixed")
            {
                var items = storage[name] as JsonObject;
                foreach (var item in ((JsonArray)setting["items"]!).OfType<JsonObject>())
                    MaskFields((JsonArray)item["fields"]!, [(items?[item["name"]!.GetValue<string>()] as JsonObject)]);
            }
            else
                MaskFields((JsonArray)setting["item"]!["fields"]!,
                    (storage[name] as JsonArray)?.OfType<JsonObject>().ToArray() ?? []);
        }
        return (definition.ToJsonString(JsonOptions), storage.ToJsonString(JsonOptions));
    }

    private static void MaskFields(JsonArray fields, JsonObject?[] values)
    {
        foreach (var field in fields.OfType<JsonObject>()) MaskField(field, values);
    }

    private static void MaskField(JsonObject field, JsonObject?[] values)
    {
        if (field["masked"]?.GetValue<bool>() != true) return;
        field.Remove("default");
        var name = field["name"]!.GetValue<string>();
        foreach (var value in values)
        {
            if (value is null) continue;
            var text = value[name] is JsonValue secret && secret.TryGetValue<string>(out var stored) ? stored : null;
            value[name] = new string('•', text?.Length ?? 0);
        }
    }
}
