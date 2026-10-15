using System.Text.Json.Nodes;
using WinPilot.Features.Plugins;
using static WinPilot.Test.PluginConfigurationTestData;

namespace WinPilot.Test;

public sealed class PluginSettingsSchemaTest
{
    [Fact]
    public void DynamicCollectionRejectsReservedMetadataFieldWithoutRestrictingOtherScopes()
    {
        const string field = """{"name":"__winpilot","type":"text"}""";
        var dynamic = "[{\"name\":\"rows\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[" + field + "]}}]";
        var error = Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(dynamic));
        Assert.Contains("__winpilot", error.Message);
        PluginSettingsSchema.ValidateDefinition("[" + field + "]");
        PluginSettingsSchema.ValidateDefinition("[{\"name\":\"rows\",\"type\":\"collection\",\"mode\":\"fixed\",\"items\":[{\"name\":\"one\",\"fields\":[" + field + "]}]}]");
        PluginSettingsSchema.ValidateDefinition("""[{"name":"rows","type":"collection","mode":"dynamic","item":{"fields":[{"name":"value","type":"text"}]}}]""");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[true]")]
    [InlineData("[{\"value\":1,\"label\":\"One\"}]")]
    [InlineData("[{\"value\":\"one\"}]")]
    [InlineData("[{\"label\":\"One\"}]")]
    [InlineData("[{\"value\":\"one\",\"label\":false}]")]
    [InlineData("[\"one\",\"one\"]")]
    [InlineData("[\"one\",{\"value\":\"one\",\"label\":\"One\"}]")]
    [InlineData("[{\"value\":\"one\",\"label\":\"One\"},{\"value\":\"one\",\"label\":\"Another\"}]")]
    public void StaticOptionsRejectInvalidShapesAndDuplicateValuesAtEveryScope(string options)
    {
        foreach (var type in new[] { "select", "multi-checkbox" })
        {
            var field = $$"""{"name":"choice","type":"{{type}}","options":{{options}}}""";
            foreach (var schema in FieldScopes(field))
                Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(schema));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("[]")]
    public void RequiredDeclarationMustBeBooleanAtEveryScope(string required)
    {
        foreach (var schema in FieldScopes($$"""{"name":"value","type":"text","required":{{required}}}"""))
            Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(schema));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(
            "[{\"name\":\"rows\",\"type\":\"collection\",\"required\":" + required + ",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"name\":\"value\",\"type\":\"text\"}]}}]"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"\",\"one\",\"One\"]")]
    [InlineData("[{\"value\":\"\",\"label\":\"None\"},{\"value\":\"one\",\"label\":\"One\"}]")]
    [InlineData("[\"one\",{\"value\":\"two\",\"label\":\"Two\"}]")]
    public void ValidStaticOptionsKeepHistoricalSelectedValuesAccepted(string options)
    {
        foreach (var type in new[] { "select", "multi-checkbox" })
        {
            var schema = $$"""[{"name":"choice","type":"{{type}}","required":false,"options":{{options}}}]""";
            PluginSettingsSchema.ValidateDefinition(schema);
            PluginSettingsSchema.ValidateConfiguration(schema, type == "select"
                ? """{"choice":"removed-choice"}""" : """{"choice":["removed-choice"]}""");
        }
        PluginSettingsSchema.ValidateDefinition("""[{"name":"value","type":"text","required":true}]""");
    }

    private static IEnumerable<string> FieldScopes(string field)
    {
        yield return "[" + field + "]";
        yield return "[{\"name\":\"rows\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[" + field + "]}}]";
        yield return "[{\"name\":\"rows\",\"type\":\"collection\",\"mode\":\"fixed\",\"items\":[{\"name\":\"one\",\"fields\":[" + field + "]}]}]";
    }

    [Fact]
    public void CollectionSchema_AllowsDefaultsAndValidValues()
    {
        const string schema = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"name\":\"name\",\"type\":\"text\",\"required\":true}]}}]";
        const string value = "{\"items\":[{\"__winpilot\":{\"id\":\"item-1\",\"remark\":\"测试\"},\"name\":\"测试\"}]}";

        PluginSettingsSchema.ValidateDefinition(schema);
        PluginSettingsSchema.ValidateConfiguration(schema, value);
    }

    [Fact]
    public void FixedCollection_AllowsMissingItemLabelButRequiresItemName()
    {
        const string schema = "[{\"name\":\"onlineManagement\",\"type\":\"collection\",\"mode\":\"fixed\",\"items\":[{\"name\":\"connection\",\"fields\":[{\"name\":\"uid\",\"type\":\"text\"}]}]}]";
        const string missingName = "[{\"name\":\"onlineManagement\",\"type\":\"collection\",\"mode\":\"fixed\",\"items\":[{\"fields\":[{\"name\":\"uid\",\"type\":\"text\"}]}]}]";

        PluginSettingsSchema.ValidateDefinition(schema);
        PluginSettingsSchema.ValidateConfiguration(schema, "{\"onlineManagement\":{\"connection\":{\"uid\":\"123\"}}}");
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(missingName));
    }

    [Fact]
    public void SettingsSchema_AllowsMaskedTextOnly()
    {
        PluginSettingsSchema.ValidateDefinition("""[{"name":"secret","type":"text","masked":true}]""");
        PluginSettingsSchema.ValidateDefinition("""[{"name":"connection","type":"collection","mode":"fixed","items":[{"name":"account","fields":[{"name":"secret","type":"text","masked":true}]}]}]""");
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"secret","type":"text","masked":"true"}]"""));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"enabled","type":"checkbox","masked":true}]"""));
    }

    [Fact]
    public void SettingsSchema_AcceptsDynamicNotificationChannelsWithoutStaticOptions()
    {
        PluginSettingsSchema.ValidateDefinition("""[{"name":"channel","type":"select","optionsSource":"notificationChannels"}]""");
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"channel","type":"select","optionsSource":"unknown"}]"""));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"channel","type":"select","optionsSource":"notificationChannels","options":["qqbot"]}]"""));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition("""[{"name":"channel","type":"text","optionsSource":"notificationChannels"}]"""));
    }

    [Fact]
    public void SettingsSchema_RejectsLegacyKeyIdentifiers()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PluginSettingsSchema.ValidateDefinition("[{\"key\":\"enabled\",\"type\":\"checkbox\",\"label\":\"启用\"}]"));
        Assert.Throws<InvalidOperationException>(() =>
            PluginSettingsSchema.ValidateDefinition("[{\"key\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"key\":\"value\",\"type\":\"text\"}]}}]"));
    }

    [Fact]
    public void CollectionSchema_RejectsInvalidCountAndNestedCollection()
    {
        var invalidCount = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[]}}]";
        var nested = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"name\":\"nested\",\"type\":\"collection\"}]}}]";

        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(invalidCount));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(nested));
    }

    [Fact]
    public void CollectionValue_EnforcesMaxItemsRequiredAndEnabled()
    {
        const string schema = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"name\":\"name\",\"type\":\"text\",\"required\":true}]}}]";
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateConfiguration(schema, "{\"items\":[{\"id\":\"1\",\"name\":\"\",\"enabled\":true}]}"));
    }

    [Fact]
    public void CollectionValue_RejectsDisabledEnableFieldAndDuplicateIds()
    {
        const string schema = "[{\"name\":\"items\",\"type\":\"collection\",\"mode\":\"dynamic\",\"item\":{\"fields\":[{\"name\":\"name\",\"type\":\"text\",\"required\":true}]}}]";

        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateConfiguration(schema, "{\"items\":[{\"id\":\"1\",\"name\":\"a\"},{\"id\":\"1\",\"name\":\"b\"}]}"));
    }

    [Theory]
    [InlineData("text", "1")]
    [InlineData("textarea", "false")]
    [InlineData("select", "[]")]
    [InlineData("number", "\"1\"")]
    [InlineData("checkbox", "\"true\"")]
    [InlineData("multi-checkbox", "[\"a\",1]")]
    [InlineData("multi-checkbox", "{}")]
    [InlineData("text", "null")]
    public void InvalidOrdinaryFieldTypesAndDefaultsAreRejected(string type, string value)
    {
        var field = new JsonObject { ["name"] = "value", ["type"] = type };
        var definition = new JsonArray(field);
        var content = new JsonObject { ["value"] = JsonNode.Parse(value) };
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateConfiguration(definition.ToJsonString(), content.ToJsonString()));
        field["default"] = JsonNode.Parse(value);
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(definition.ToJsonString()));
        var nested = new JsonArray(new JsonObject
        {
            ["name"] = "fixed", ["type"] = "collection", ["mode"] = "fixed",
            ["items"] = new JsonArray(new JsonObject { ["name"] = "group", ["fields"] = definition.DeepClone() })
        });
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(nested.ToJsonString()));
    }

    [Fact]
    public void RequiredOrdinaryFieldsAreCheckedOnlyWhenSaving()
    {
        const string schema = """[{"name":"value","type":"text","required":true,"default":""}]""";
        PluginSettingsSchema.ValidateDefinition(schema);
        var defaults = PluginSettingsSchema.ApplyDefaults(schema, new JsonObject());
        Assert.Equal("", defaults["value"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateConfiguration(schema, "{}"));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateConfiguration(schema, defaults.ToJsonString()));
        PluginSettingsSchema.ValidateConfiguration(schema, """{"value":"已填写"}""");
    }

    [Fact]
    public void EmptyNumbersUseDefaultsWithoutChangingZeroOrEmptyText()
    {
        const string schema = """
            [{"name":"number","type":"number","default":5,"required":true},
             {"name":"zero","type":"number","default":5},{"name":"text","type":"text"},
             {"name":"fixed","type":"collection","mode":"fixed","items":[
               {"name":"group","fields":[{"name":"number","type":"number"}]}]},
             {"name":"rows","type":"collection","mode":"dynamic","item":{"fields":[
               {"name":"number","type":"number","default":2.5}]}}]
            """;
        var config = PluginSettingsSchema.ApplyDefaults(schema, JsonNode.Parse(
            """{"number":"","zero":0,"text":"","fixed":{"group":{"number":""}},"rows":[{"number":"","__winpilot":{"id":"row","remark":"条目"}}]}""")!.AsObject());
        Assert.Equal(5, config["number"]!.GetValue<int>());
        Assert.Equal(0, config["zero"]!.GetValue<int>());
        Assert.Equal("", config["text"]!.GetValue<string>());
        Assert.Equal(0, config["fixed"]!["group"]!["number"]!.GetValue<int>());
        Assert.Equal(2.5, config["rows"]![0]!["number"]!.GetValue<double>());
        PluginSettingsSchema.ValidateConfiguration(schema, config.ToJsonString());
    }

    [Theory]
    [InlineData("\"min\":\"0\"")]
    [InlineData("\"max\":null")]
    [InlineData("\"min\":1e400")]
    [InlineData("\"min\":2,\"max\":1")]
    [InlineData("\"min\":1")]
    [InlineData("\"step\":0")]
    [InlineData("\"step\":-1")]
    [InlineData("\"step\":\"1\"")]
    [InlineData("\"step\":true")]
    [InlineData("\"max\":-1")]
    [InlineData("\"step\":2,\"default\":1")]
    public void InvalidNumberConstraintsOrDefaultsAreRejected(string constraints)
    {
        var schema = "[{\"name\":\"value\",\"type\":\"number\"," + constraints + "}]";
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(schema));
        var fields = JsonNode.Parse(schema)!.AsArray();
        var collection = new JsonArray(new JsonObject
        {
            ["name"] = "group", ["type"] = "collection", ["mode"] = "dynamic",
            ["item"] = new JsonObject { ["fields"] = fields }
        });
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(collection.ToJsonString()));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("checkbox")]
    public void NumericConstraintsAreRejectedOnOtherFieldTypes(string type)
    {
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateDefinition(
            $$"""[{"name":"value","type":"{{type}}","min":0}]"""));
    }

    [Theory]
    [InlineData("0.1", true)]
    [InlineData("0.3", true)]
    [InlineData("0.30000000000000004", true)]
    [InlineData("0.5", true)]
    [InlineData("0", false)]
    [InlineData("0.6", false)]
    [InlineData("0.2", false)]
    public void NumericLimitsApplyToOrdinaryAndCollectionFields(string number, bool valid)
    {
        const string field = """{"name":"value","type":"number","min":0.1,"max":0.5,"step":0.2,"default":0.1}""";
        foreach (var mode in new[] { "ordinary", "fixed", "dynamic" })
        {
            var schema = mode switch
            {
                "fixed" => $$"""[{"name":"group","type":"collection","mode":"fixed","items":[{"name":"item","fields":[{{field}}]}]}]""",
                "dynamic" => $$$"""[{"name":"group","type":"collection","mode":"dynamic","item":{"fields":[{{{field}}}]}}]""",
                _ => "[" + field + "]"
            };
            var content = mode switch
            {
                "fixed" => "{\"group\":{\"item\":{\"value\":" + number + "}}}",
                "dynamic" => "{\"group\":[{\"value\":" + number + ",\"__winpilot\":{\"id\":\"item\",\"remark\":\"条目\"}}]}",
                _ => "{\"value\":" + number + "}"
            };
            PluginSettingsSchema.ValidateDefinition(schema);
            if (valid) PluginSettingsSchema.ValidateConfiguration(schema, content);
            else Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateConfiguration(schema, content));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"step\":\"any\"")]
    public void UndeclaredStepOrAnyAllowsFractionalValues(string constraint)
    {
        var schema = "[{\"name\":\"value\",\"type\":\"number\"" + constraint + "}]";
        PluginSettingsSchema.ValidateDefinition(schema);
        PluginSettingsSchema.ValidateConfiguration(schema, """{"value":0.25}""");
    }

    [Theory]
    [InlineData("\"延迟秒数\"", "延迟秒数")]
    [InlineData("\"\"", "interval")]
    [InlineData("null", "interval")]
    public void ConfigurationValidationUsesFieldLabelWithNameFallback(string label, string expectedName)
    {
        var schema = $$"""[{"name":"interval","label":{{label}},"type":"number","min":1,"max":5,"step":2,"default":1,"required":true}]""";
        var cases = new[]
        {
            ("{}", "不能为空"), ("{\"interval\":\"bad\"}", "格式无效"),
            ("{\"interval\":0}", "不能小于 1"), ("{\"interval\":6}", "不能大于 5"),
            ("{\"interval\":2}", "不符合步长 2")
        };
        foreach (var (content, message) in cases)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateConfiguration(schema, content));
            Assert.Equal(expectedName + message, exception.Message);
        }
    }

    [Theory]
    [InlineData("{\"rows\":{}}", "路线配置格式无效，请重新打开配置")]
    [InlineData("{\"rows\":[1]}", "路线配置包含无效条目，请重新添加")]
    [InlineData("{\"rows\":[{}]}", "路线配置条目信息不完整，请重新添加")]
    [InlineData("{\"rows\":[{\"__winpilot\":{\"id\":\"item\"}}]}", "备注不能为空")]
    [InlineData("{\"rows\":[{\"__winpilot\":{\"id\":\"item\",\"remark\":\"条目\"}},{\"__winpilot\":{\"id\":\"item\",\"remark\":\"条目\"}}]}", "路线配置包含重复条目，请删除后重新添加")]
    public void CollectionConfigurationErrorsUseDisplayNamesAndActionableMessages(string content, string message)
    {
        const string schema = """[{"name":"rows","label":"路线配置","type":"collection","mode":"dynamic","item":{"fields":[{"name":"value","type":"text"}]}}]""";
        var exception = Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateConfiguration(schema, content));
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void DefaultsFollowFieldTypesAndPreserveSavedValues()
    {
        PluginSettingsSchema.ValidateDefinition(Definition);
        var defaults = PluginSettingsSchema.ApplyDefaults(Definition, new JsonObject());
        Assert.Equal("", defaults["text"]!.GetValue<string>());
        Assert.True(defaults["toggle"]!.GetValue<bool>());
        Assert.Equal(0, defaults["count"]!.GetValue<int>());
        Assert.Empty(defaults["choices"]!.AsArray());
        Assert.Empty(defaults["rows"]!.AsArray());
        Assert.Equal("", defaults["fixed"]!["group"]!["target"]!.GetValue<string>());
        Assert.False(defaults["fixed"]!["group"]!["enabled"]!.GetValue<bool>());
        var saved = JsonNode.Parse("""{"toggle":false,"text":"已保存","extra":42,"rows":[{"text":"条目","__winpilot":{"id":"id","remark":"备注"}}]}""")!.AsObject();
        PluginSettingsSchema.ApplyDefaults(Definition, saved);
        Assert.False(saved["toggle"]!.GetValue<bool>());
        Assert.Equal("已保存", saved["text"]!.GetValue<string>());
        Assert.Equal(42, saved["extra"]!.GetValue<int>());
        Assert.True(saved["rows"]![0]!["enabled"]!.GetValue<bool>());
        PluginSettingsSchema.ValidateConfiguration(Definition, saved.ToJsonString());
    }
}
