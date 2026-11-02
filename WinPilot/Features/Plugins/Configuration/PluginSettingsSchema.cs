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
                ApplyFieldDefault(setting, content);
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
            ApplyFieldDefault(field, content);
    }

    private static void ApplyFieldDefault(JsonElement field, JsonObject content)
    {
        var name = field.GetProperty("name").GetString()!;
        if (!content.ContainsKey(name) || field.GetProperty("type").GetString() == "number"
            && content[name] is JsonValue value && value.TryGetValue<string>(out var text) && text == "")
            content[name] = DefaultValue(field);
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
            if (type != "collection") ValidateFieldDefinition(setting, type, name);
            else ValidateRequired(setting, name);
            if (!names.Add(name)) throw new InvalidOperationException($"配置项名称重复：{name}");
            if (type == "collection") ValidateCollectionDefinition(setting);
        }
    }

    public static void ValidateConfiguration(string definitionJson, string contentJson)
    {
        using var definition = JsonDocument.Parse(definitionJson); using var content = JsonDocument.Parse(contentJson);
        if (content.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("插件配置格式无效，请重新打开配置");
        foreach (var setting in definition.RootElement.EnumerateArray())
        {
            if (!IsCollection(setting))
            {
                ValidateFieldValue(setting, content.RootElement);
                continue;
            }
            ValidateCollectionDefinition(setting); var name = RequiredString(setting, "name", "Collection 缺少 name");
            if (content.RootElement.TryGetProperty(name, out var value)) ValidateCollectionValue(setting, value);
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
            if (setting.TryGetProperty("items", out _)) throw new InvalidOperationException("Dynamic Collection 不允许声明 items"); ValidateFields(item, dynamic: true);
        }
        else throw new InvalidOperationException("Collection mode 只能是 fixed 或 dynamic");
    }

    private static void ValidateFields(JsonElement owner, bool dynamic = false)
    {
        if (!owner.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() == 0) throw new InvalidOperationException("Collection 必须声明非空 fields 数组");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Collection fields 必须包含对象");
            var name = RequiredString(field, "name", "Collection 字段缺少 name");
            if (dynamic && name == "__winpilot") throw new InvalidOperationException("Dynamic Collection 字段不能使用宿主保留名称 __winpilot");
            if (!names.Add(name)) throw new InvalidOperationException($"Collection 字段 name 重复：{name}");
            var type = RequiredString(field, "type", $"Collection 字段 {name} 缺少 type");
            if (!FieldTypes.Contains(type)) throw new InvalidOperationException($"Collection 字段 {name} 类型无效或不支持嵌套 Collection：{type}");
            ValidateMasked(field, type, name);
            ValidateOptionsSource(field, type, name);
            ValidateFieldDefinition(field, type, name);
        }
    }

    private static void ValidateCollectionValue(JsonElement definition, JsonElement value)
    {
        var displayName = FieldDisplayName(definition);
        var mode = definition.GetProperty("mode").GetString();
        if (mode == "fixed")
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{displayName}格式无效，请重新打开配置");
            foreach (var item in definition.GetProperty("items").EnumerateArray()) { var itemName = item.GetProperty("name").GetString()!; if (value.TryGetProperty(itemName, out var itemValue)) ValidateFieldObject(displayName, item.GetProperty("fields"), itemValue); }
        }
        else
        {
            if (value.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"{displayName}格式无效，请重新打开配置");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{displayName}包含无效条目，请重新添加");
                if (!item.TryGetProperty("__winpilot", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"{displayName}条目信息不完整，请重新添加");
                var id = RequiredString(metadata, "id", $"{displayName}条目信息不完整，请重新添加");
                if (!ids.Add(id)) throw new InvalidOperationException($"{displayName}包含重复条目，请删除后重新添加");
                RequiredString(metadata, "remark", "备注不能为空");
                ValidateFieldObject(displayName, definition.GetProperty("item").GetProperty("fields"), item);
            }
        }
    }

    private static void ValidateFieldObject(string collectionName, JsonElement fields, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{collectionName}包含无效条目，请重新打开配置");
        foreach (var field in fields.EnumerateArray()) ValidateFieldValue(field, value);
    }

    private static void ValidateFieldValue(JsonElement field, JsonElement content)
    {
        var name = field.GetProperty("name").GetString()!;
        var displayName = FieldDisplayName(field);
        if (!content.TryGetProperty(name, out var value))
        {
            if (IsRequired(field)) throw new InvalidOperationException($"{displayName}不能为空");
            return;
        }
        if (IsRequired(field) && IsEmpty(value)) throw new InvalidOperationException($"{displayName}不能为空");
        if (!IsValidFieldValue(field.GetProperty("type").GetString(), value))
            throw new InvalidOperationException($"{displayName}格式无效");
        if (field.GetProperty("type").GetString() == "number") ValidateNumberValue(field, value, displayName);
    }

    private static void ValidateFieldDefinition(JsonElement field, string type, string name)
    {
        ValidateRequired(field, name);
        if (field.TryGetProperty("options", out var options)) ValidateOptions(options, name);
        if (field.TryGetProperty("default", out var value) && !IsValidFieldValue(type, value))
            throw new InvalidOperationException($"配置项 {name} 的默认值类型无效");
        foreach (var key in new[] { "min", "max", "step" })
        {
            if (!field.TryGetProperty(key, out var limit)) continue;
            if (type != "number") throw new InvalidOperationException($"配置项 {name} 的 {key} 仅支持 number 类型");
            if (key == "step" && limit.ValueKind == JsonValueKind.String && limit.GetString() == "any") continue;
            if (limit.ValueKind != JsonValueKind.Number || !limit.TryGetDouble(out var number) || !double.IsFinite(number)
                || key == "step" && number <= 0)
                throw new InvalidOperationException($"配置项 {name} 的 {key} 无效");
        }
        if (type != "number") return;
        if (field.TryGetProperty("min", out var min) && field.TryGetProperty("max", out var max) && min.GetDouble() > max.GetDouble())
            throw new InvalidOperationException($"配置项 {name} 的 min 不能大于 max");
        ValidateNumberValue(field, field.TryGetProperty("default", out value) ? value : JsonSerializer.SerializeToElement(0), name);
    }

    private static void ValidateRequired(JsonElement field, string name)
    {
        if (field.TryGetProperty("required", out var required) && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException($"配置项 {name} 的 required 必须是布尔值");
    }

    private static void ValidateOptions(JsonElement options, string name)
    {
        if (options.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"配置项 {name} 的 options 必须是数组");
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options.EnumerateArray())
        {
            string value;
            if (option.ValueKind == JsonValueKind.String) value = option.GetString()!;
            else if (option.ValueKind == JsonValueKind.Object
                && option.TryGetProperty("value", out var key) && key.ValueKind == JsonValueKind.String
                && option.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                value = key.GetString()!;
            else throw new InvalidOperationException($"配置项 {name} 的 options 必须包含字符串或带有字符串 value、label 的对象");
            if (!values.Add(value)) throw new InvalidOperationException($"配置项 {name} 的 options 存在重复值：{value}");
        }
    }

    private static void ValidateNumberValue(JsonElement field, JsonElement value, string name)
    {
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidOperationException($"{name}必须是有效数字");
        var minimum = field.TryGetProperty("min", out var min) ? min.GetDouble() : (double?)null;
        if (minimum.HasValue && number < minimum.Value)
            throw new InvalidOperationException($"{name}不能小于 {minimum.Value}");
        if (field.TryGetProperty("max", out var max) && number > max.GetDouble())
            throw new InvalidOperationException($"{name}不能大于 {max.GetDouble()}");
        if (!field.TryGetProperty("step", out var step) || step.ValueKind == JsonValueKind.String) return;
        var steps = (number - (minimum ?? 0)) / step.GetDouble();
        if (!double.IsFinite(steps) || Math.Abs(steps - Math.Round(steps)) > 1e-7)
            throw new InvalidOperationException($"{name}不符合步长 {step.GetDouble()}");
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
    private static string FieldDisplayName(JsonElement field) => field.TryGetProperty("label", out var label)
        && label.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(label.GetString())
            ? label.GetString()! : field.GetProperty("name").GetString()!;
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
