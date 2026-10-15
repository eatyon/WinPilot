using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinPilot.Web;

public static class PluginConfigurationResponse
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static (string Definition, string Configuration) MaskSensitiveValues(string definitionJson, string configurationJson)
    {
        var definition = JsonNode.Parse(definitionJson) as JsonArray
            ?? throw new InvalidOperationException("settings.json 必须是数组");
        var configuration = JsonNode.Parse(configurationJson) as JsonObject
            ?? throw new InvalidOperationException("插件配置格式无效");
        RemoveSensitiveDefaults(definition);
        return (definition.ToJsonString(JsonOptions), ProjectFields(definition, configuration).ToJsonString(JsonOptions));
    }

    private static void RemoveSensitiveDefaults(JsonArray fields)
    {
        foreach (var field in fields.OfType<JsonObject>())
        {
            if (field["masked"]?.GetValue<bool>() == true) field.Remove("default");
            if (field["type"]?.GetValue<string>() != "collection") continue;
            if (field["mode"]?.GetValue<string>() == "fixed")
            {
                foreach (var item in ((JsonArray)field["items"]!).OfType<JsonObject>())
                    RemoveSensitiveDefaults((JsonArray)item["fields"]!);
            }
            else RemoveSensitiveDefaults((JsonArray)field["item"]!["fields"]!);
        }
    }

    private static JsonObject ProjectFields(JsonArray fields, JsonObject source)
    {
        var result = new JsonObject();
        foreach (var field in fields.OfType<JsonObject>())
        {
            var name = field["name"]!.GetValue<string>();
            if (field["type"]?.GetValue<string>() == "collection")
            {
                if (field["mode"]?.GetValue<string>() == "fixed" && source[name] is JsonObject fixedItems)
                {
                    var projected = new JsonObject();
                    foreach (var item in ((JsonArray)field["items"]!).OfType<JsonObject>())
                    {
                        var itemName = item["name"]!.GetValue<string>();
                        if (fixedItems[itemName] is JsonObject values)
                            projected[itemName] = ProjectFields((JsonArray)item["fields"]!, values);
                    }
                    result[name] = projected;
                }
                else if (field["mode"]?.GetValue<string>() == "dynamic" && source[name] is JsonArray dynamicItems)
                {
                    var projected = new JsonArray();
                    foreach (var item in dynamicItems.OfType<JsonObject>())
                    {
                        var values = ProjectFields((JsonArray)field["item"]!["fields"]!, item);
                        if (item["__winpilot"] is JsonObject metadata)
                        {
                            var projectedMetadata = new JsonObject();
                            foreach (var key in new[] { "id", "remark" })
                                if (metadata[key]?.GetValueKind() == JsonValueKind.String)
                                    projectedMetadata[key] = metadata[key]!.DeepClone();
                            values["__winpilot"] = projectedMetadata;
                        }
                        projected.Add(values);
                    }
                    result[name] = projected;
                }
                continue;
            }
            var masked = field["masked"]?.GetValue<bool>() == true;
            if (!source.TryGetPropertyValue(name, out var value))
            {
                if (masked) result[name] = "";
                continue;
            }
            if (!IsFieldValue(field["type"]!.GetValue<string>(), value)) continue;
            result[name] = masked ? JsonValue.Create(new string('•', value!.GetValue<string>().Length)) : value!.DeepClone();
        }
        return result;
    }

    private static bool IsFieldValue(string type, JsonNode? value) => type switch
    {
        "text" or "textarea" or "select" => value?.GetValueKind() == JsonValueKind.String,
        "number" => value?.GetValueKind() == JsonValueKind.Number,
        "checkbox" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "multi-checkbox" => value is JsonArray items && items.All(item => item?.GetValueKind() == JsonValueKind.String),
        _ => false
    };
}
