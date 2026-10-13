using System.Text.Json;
using System.Text.Json.Nodes;
using WinPilot.Features.Notifications;

namespace WinPilot.Features.Plugins;

public static class PluginSettingOptionSources
{
    public const string NotificationChannels = "notificationChannels";

    private static readonly IReadOnlyList<PluginSettingOption> ChannelOptions =
        NotificationChannelCatalog.All
            .Select(channel => new PluginSettingOption(channel.Id.ToLowerInvariant(), channel.DisplayName))
            .Prepend(new(NotificationChannelCatalog.AllEnabled, NotificationChannelCatalog.EnabledDisplayName)).ToArray();

    public static IReadOnlyList<PluginSettingOption> GetOptions(string source) => source switch
    {
        NotificationChannels => ChannelOptions,
        _ => throw new InvalidOperationException($"不支持的配置选项来源：{source}")
    };

    public static string WithSelectedOptions(string definitionJson, string? storageJson, JsonSerializerOptions? options = null)
    {
        var definition = JsonNode.Parse(definitionJson) as JsonArray ?? throw new InvalidOperationException("settings.json 必须是数组");
        var storage = string.IsNullOrWhiteSpace(storageJson) ? null : JsonNode.Parse(storageJson) as JsonObject;
        foreach (var item in definition.OfType<JsonObject>())
        {
            if (item["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name)) continue;
            HydrateField(item, [storage?[name]]);
        }
        return definition.ToJsonString(options);
    }

    private static void HydrateField(JsonObject field, IEnumerable<JsonNode?> storedValues)
    {
        var values = storedValues.ToArray();
        if (ReadString(field["optionsSource"]) is { } source)
        {
            var selectedValues = values.Select(ReadString).Append(ReadString(field["default"]))
                .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
            if (selectedValues.Length == 0) return;
            var choices = GetOptions(source);
            field["options"] = new JsonArray(selectedValues.Select(value =>
            {
                var selected = choices.FirstOrDefault(option => option.Value == value) ?? new PluginSettingOption(value!, value!);
                return (JsonNode?)new JsonObject { ["value"] = selected.Value, ["label"] = selected.Label };
            }).ToArray());
            return;
        }
        if (ReadString(field["type"]) != "collection") return;
        if (ReadString(field["mode"]) == "fixed" && field["items"] is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                var name = ReadString(item["name"]);
                if (name is null || item["fields"] is not JsonArray fields) continue;
                HydrateFields(fields, values.Select(value => (value as JsonObject)?[name]));
            }
        }
        else if (field["item"] is JsonObject item && item["fields"] is JsonArray fields)
        {
            HydrateFields(fields, values.OfType<JsonArray>().SelectMany(value => value));
        }
    }

    private static void HydrateFields(JsonArray fields, IEnumerable<JsonNode?> storedItems)
    {
        var items = storedItems.OfType<JsonObject>().ToArray();
        foreach (var field in fields.OfType<JsonObject>())
        {
            var name = ReadString(field["name"]);
            if (name is not null) HydrateField(field, items.Select(item => item[name]));
        }
    }

    private static string? ReadString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public sealed record PluginSettingOption(string Value, string Label);
