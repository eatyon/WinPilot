using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinPilot.Features.Plugins;

public static class PluginSettingsSchema
{
    private static readonly HashSet<string> FieldTypes = ["text", "number", "select", "checkbox", "multi-checkbox", "textarea"];

    public static JsonObject ApplyDefaults(string definitionJson, JsonObject content)
    {
        using var definition = JsonDocument.Parse(definitionJson);
        foreach (var setting in definition.RootElement.EnumerateArray())
        {
            var name = setting.GetProperty("name").GetString()!;
            if (!IsCollection(setting))
            {
                if (!content.ContainsKey(name)) content[name] = DefaultValue(setting);
                continue;
            }
            if (setting.GetProperty("mode").GetString() == "dynamic")
            {
                if (!content.ContainsKey(name)) content[name] = new JsonArray();
                if (content[name] is JsonArray items)
                    foreach (var item in items.OfType<JsonObject>())
                        ApplyFieldDefaults(setting.GetProperty("item").GetProperty("fields"), item);
                continue;
            }
            if (!content.ContainsKey(name)) content[name] = new JsonObject();
            if (content[name] is not JsonObject fixedItems) continue;
            foreach (var item in setting.GetProperty("items").EnumerateArray())
            {
                var itemName = item.GetProperty("name").GetString()!;
                if (!fixedItems.ContainsKey(itemName)) fixedItems[itemName] = new JsonObject();
                if (fixedItems[itemName] is JsonObject fields)
                    ApplyFieldDefaults(item.GetProperty("fields"), fields);
            }
        }
        return content;
    }

    private static void ApplyFieldDefaults(JsonElement fields, JsonObject content)
    {
        foreach (var field in fields.EnumerateArray())
        {
            var name = field.GetProperty("name").GetString()!;
            if (!content.ContainsKey(name)) content[name] = DefaultValue(field);
        }
    }

    private static JsonNode? DefaultValue(JsonElement field)
    {
        if (field.TryGetProperty("default", out var value)) return JsonNode.Parse(value.GetRawText());
        return field.GetProperty("type").GetString() switch
        {
            "checkbox" => JsonValue.Create(false),
            "multi-checkbox" => new JsonArray(),
            "number" => JsonValue.Create(0),
            _ => JsonValue.Create("")
        };
    }

    public static void ValidateDefinition(string definitionJson)
    {
        using var document = JsonDocument.Parse(definitionJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("settings.json 必须是数组");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in document.RootElement.EnumerateArray())
        {
            if (setting.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("settings.json 配置项必须是对象");
            var type = RequiredString(setting, "type", "配置项缺少 type");
            if (type != "collection" && !FieldTypes.Contains(type)) throw new InvalidOperationException($"配置项类型无效：{type}");
            var name = RequiredString(setting, "name", "配置项缺少 name");
            ValidateMasked(setting, type, name);
            ValidateOptionsSource(setting, type, name);
            if (type != "collection") ValidateDefault(setting, type, name);
            if (!names.Add(name)) throw new InvalidOperationException($"配置项名称重复：{name}");
            if (type == "collection") ValidateCollectionDefinition(setting);
        }
    }

    public static void ValidateStorage(string definitionJson, string contentJson)
    {
        using var definition = JsonDocument.Parse(definitionJson); using var content = JsonDocument.Parse(contentJson);
        if (content.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("插件配置必须是 JSON 对象");
        foreach (var setting in definition.RootElement.EnumerateArray())
        {
            if (!IsCollection(setting))
            {
                ValidateFieldValue(setting, content.RootElement, "插件配置");
                continue;
            }
            ValidateCollectionDefinition(setting); var name = RequiredString(setting, "name", "Collection 缺少 name");
            if (content.RootElement.TryGetProperty(name, out var value)) ValidateCollectionValue(name, setting, value);
        }
    }

    private static void ValidateCollectionDefinition(JsonElement setting)
    {
        RequiredString(setting, "name", "Collection 缺少 name"); var mode = RequiredString(setting, "mode", "Collection 必须声明 mode");
        if (mode == "fixed")
        {
            if (!setting.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Fixed Collection 必须声明 items 数组");
            if (setting.TryGetProperty("item", out _)) throw new InvalidOperationException("Fixed Collection 不允许声明 item");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray()) { if (item.ValueKind != JsonValueKind.Object || !names.Add(RequiredString(item, "name", "Fixed Item 缺少或重复 name"))) throw new InvalidOperationException("Fixed Item name 重复或无效"); if (item.TryGetProperty("label", out _)) RequiredString(item, "label", "Fixed Item label 无效"); ValidateFields(item); }
        }
        else if (mode == "dynamic")
        {
            if (!setting.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("fields", out _)) throw new InvalidOperationException("Dynamic Collection 必须声明 item.fields");
            if (setting.TryGetProperty("items", out _)) throw new InvalidOperationException("Dynamic Collection 不允许声明 items"); ValidateFields(item);
        }
        else throw new InvalidOperationException("Collection mode 只能是 fixed 或 dynamic");
    }

    private static void ValidateFields(JsonElement owner)
    {
        if (!owner.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() == 0) throw new InvalidOperationException("Collection 必须声明非空 fields 数组");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Collection fields 必须包含对象");
            var name = RequiredString(field, "name", "Collection 字段缺少 name");
            if (!names.Add(name)) throw new InvalidOperationException($"Collection 字段 name 重复：{name}");
            var type = RequiredString(field, "type", $"Collection 字段 {name} 缺少 type");
            if (!FieldTypes.Contains(type)) throw new InvalidOperationException($"Collection 字段 {name} 类型无效或不支持嵌套 Collection：{type}");
            ValidateMasked(field, type, name);
            ValidateOptionsSource(field, type, name);
            ValidateDefault(field, type, name);
        }
    }

    private static void ValidateCollectionValue(string name, JsonElement definition, JsonElement value)
    {
        var mode = definition.GetProperty("mode").GetString();
        if (mode == "fixed")
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Fixed Collection {name} 必须是对象");
            foreach (var item in definition.GetProperty("items").EnumerateArray()) { var itemName = item.GetProperty("name").GetString()!; if (value.TryGetProperty(itemName, out var itemValue)) ValidateFieldObject(name, item.GetProperty("fields"), itemValue); }
        }
        else
        {
            if (value.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"Dynamic Collection {name} 必须是数组");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray()) { if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Dynamic Item 必须是对象"); if (!item.TryGetProperty("__winpilot", out var metadata) || metadata.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Dynamic Item 缺少 __winpilot 元数据"); var id = RequiredString(metadata, "id", "Dynamic Item 缺少 __winpilot.id"); if (!ids.Add(id)) throw new InvalidOperationException($"Dynamic Collection 存在重复 __winpilot.id：{id}"); RequiredString(metadata, "remark", "Dynamic Item 的 __winpilot.remark 不能为空"); ValidateFieldObject(name, definition.GetProperty("item").GetProperty("fields"), item); }
        }
    }

    private static void ValidateFieldObject(string collectionKey, JsonElement fields, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Collection {collectionKey} Item 必须是对象");
        foreach (var field in fields.EnumerateArray()) ValidateFieldValue(field, value, $"Collection {collectionKey}");
    }

    private static void ValidateFieldValue(JsonElement field, JsonElement content, string owner)
    {
        var name = field.GetProperty("name").GetString()!;
        if (!content.TryGetProperty(name, out var value))
        {
            if (IsRequired(field)) throw new InvalidOperationException($"{owner} 的 {name} 不能为空");
            return;
        }
        if (!IsValidFieldValue(field.GetProperty("type").GetString(), value) || IsRequired(field) && IsEmpty(value))
            throw new InvalidOperationException($"{owner} 的 {name} 类型无效或为空");
    }

    private static void ValidateDefault(JsonElement field, string type, string name)
    {
        if (field.TryGetProperty("default", out var value) && !IsValidFieldValue(type, value))
            throw new InvalidOperationException($"配置项 {name} 的默认值类型无效");
    }

    private static bool IsValidFieldValue(string? type, JsonElement value) => type switch
    {
        "text" or "textarea" or "select" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "checkbox" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "multi-checkbox" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
        _ => false
    };

    private static bool IsCollection(JsonElement value) => value.TryGetProperty("type", out var type) && type.GetString() == "collection";
    private static void ValidateMasked(JsonElement field, string type, string name)
    {
        if (!field.TryGetProperty("masked", out var masked)) return;
        if (type != "text" || masked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException($"配置项 {name} 的 masked 只支持 text 类型的布尔值");
    }
    private static void ValidateOptionsSource(JsonElement field, string type, string name)
    {
        if (!field.TryGetProperty("optionsSource", out var source)) return;
        if (type != "select" || source.ValueKind != JsonValueKind.String || source.GetString() != PluginSettingOptionSources.NotificationChannels || field.TryGetProperty("options", out _))
            throw new InvalidOperationException($"配置项 {name} 的 optionsSource 无效，且不能与 options 同时声明");
    }
    private static bool IsRequired(JsonElement value) => value.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True;
    private static bool IsEmpty(JsonElement value) => value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()) || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;
    private static string RequiredString(JsonElement value, string key, string error) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! : throw new InvalidOperationException(error);
}
