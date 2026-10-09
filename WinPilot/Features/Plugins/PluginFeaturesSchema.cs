using System.Text.Json;

namespace WinPilot.Features.Plugins;

public static class PluginFeaturesSchema
{
    private static readonly HashSet<string> Modes = ["action", "page"];

    public static void ValidateDefinition(string definitionJson)
    {
        using var document = JsonDocument.Parse(definitionJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("features.json 必须包含 features 数组");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("features 必须包含对象");
            var id = RequiredString(feature, "id", "Feature 缺少 id");
            if (!ids.Add(id)) throw new InvalidOperationException($"Feature id 重复：{id}");
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9]+(?:-[a-z0-9]+)*$")) throw new InvalidOperationException($"Feature id 格式无效：{id}");
            ValidateLength(RequiredString(feature, "name", $"Feature {id} 缺少 name"), 100, $"Feature {id} 的 name 不能超过 100 个字符");
            if (feature.TryGetProperty("description", out var description))
            {
                if (description.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"Feature {id} 的 description 必须是字符串");
                ValidateLength(description.GetString() ?? string.Empty, 500, $"Feature {id} 的 description 不能超过 500 个字符");
            }
            var mode = RequiredString(feature, "mode", $"Feature {id} 缺少 mode");
            if (!Modes.Contains(mode)) throw new InvalidOperationException($"Feature {id} 的 mode 必须是 action 或 page");
            if (mode == "page")
            {
                var entry = RequiredString(feature, "entry", $"Feature {id} 的 {mode} 缺少 entry");
                ValidateLength(entry, 200, $"Feature {id} 的 entry 不能超过 200 个字符");
                if (Path.IsPathRooted(entry) || entry.Contains("..", StringComparison.Ordinal)) throw new InvalidOperationException($"Feature {id} 的 entry 路径无效");
                if (!entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Feature {id} 的 page entry 必须是 JS 模块");
            }
            if (feature.TryGetProperty("confirm", out var confirm) && confirm.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                throw new InvalidOperationException($"Feature {id} 的 confirm 必须是布尔值");
            if (mode != "action" && feature.TryGetProperty("confirm", out _)) throw new InvalidOperationException($"Feature {id} 的 {mode} 不支持 confirm");
        }
    }

    private static string RequiredString(JsonElement value, string key, string error) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! : throw new InvalidOperationException(error);
    private static void ValidateLength(string value, int maximumLength, string error) { if (value.Length > maximumLength) throw new InvalidOperationException(error); }
}
