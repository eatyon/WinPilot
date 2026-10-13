using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Plugins;
using WinPilot.Features.Plugins;

namespace WinPilot.Test;

public sealed class PluginConfigurationTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winpilot-plugin-config", Guid.NewGuid().ToString("N"));
    private readonly PluginConfigurationStore configurationStore = new();
    private const string Definition = """
        [
          {"name":"text","type":"text"},
          {"name":"toggle","type":"checkbox","default":true},
          {"name":"count","type":"number"},
          {"name":"choices","type":"multi-checkbox"},
          {"name":"fixed","type":"collection","mode":"fixed","items":[
            {"name":"group","fields":[{"name":"target","type":"text"},{"name":"enabled","type":"checkbox"}]}]},
          {"name":"rows","type":"collection","mode":"dynamic","item":{"fields":[
            {"name":"text","type":"text"},{"name":"enabled","type":"checkbox","default":true}]}}
        ]
        """;

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
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateStorage(definition.ToJsonString(), content.ToJsonString()));
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
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateStorage(schema, "{}"));
        Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateStorage(schema, defaults.ToJsonString()));
        PluginSettingsSchema.ValidateStorage(schema, """{"value":"已填写"}""");
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
        PluginSettingsSchema.ValidateStorage(schema, config.ToJsonString());
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
            if (valid) PluginSettingsSchema.ValidateStorage(schema, content);
            else Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateStorage(schema, content));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"step\":\"any\"")]
    public void UndeclaredStepOrAnyAllowsFractionalValues(string constraint)
    {
        var schema = "[{\"name\":\"value\",\"type\":\"number\"" + constraint + "}]";
        PluginSettingsSchema.ValidateDefinition(schema);
        PluginSettingsSchema.ValidateStorage(schema, """{"value":0.25}""");
    }

    [Theory]
    [InlineData("\"延迟秒数\"", "延迟秒数")]
    [InlineData("\"\"", "interval")]
    [InlineData("null", "interval")]
    public void StorageValidationUsesFieldLabelWithNameFallback(string label, string expectedName)
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
            var exception = Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateStorage(schema, content));
            Assert.Equal(expectedName + message, exception.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationInitializationFailureDoesNotDiscardOtherPlugins(bool blockDataDirectory)
    {
        var paths = PortableDataPaths.Create(root);
        foreach (var id in new[] { "broken", "healthy" })
        {
            var directory = Path.Combine(paths.PluginsDirectory, id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "manifest.json"),
                $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
            File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
            File.WriteAllText(Path.Combine(directory, "settings.json"), Definition);
        }
        Directory.CreateDirectory(paths.PluginDataDirectory);
        var brokenData = Path.Combine(paths.PluginDataDirectory, "broken");
        if (blockDataDirectory) File.WriteAllText(brokenData, "占用目录路径");
        else Directory.CreateDirectory(Path.Combine(brokenData, "config.json.tmp"));
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, new Runtime(), pages);
        Assert.Equal(2, manager.GetPlugins().Count);
        var broken = manager.GetPlugin("broken");
        Assert.Equal(PluginState.Invalid, broken.State);
        Assert.False(broken.CanChangeEnabled);
        Assert.Contains(broken.Errors, error => error.Contains("配置初始化失败"));
        manager.Enable("healthy");
        Assert.Equal(PluginState.Running, manager.GetPlugin("healthy").State);
        manager.Disable("healthy");
        manager.Reload();
        Assert.Equal(2, manager.GetPlugins().Count);
    }

    [Theory]
    [InlineData("{\"rows\":{}}", "路线配置格式无效，请重新打开配置")]
    [InlineData("{\"rows\":[1]}", "路线配置包含无效条目，请重新添加")]
    [InlineData("{\"rows\":[{}]}", "路线配置条目信息不完整，请重新添加")]
    [InlineData("{\"rows\":[{\"__winpilot\":{\"id\":\"item\"}}]}", "备注不能为空")]
    [InlineData("{\"rows\":[{\"__winpilot\":{\"id\":\"item\",\"remark\":\"条目\"}},{\"__winpilot\":{\"id\":\"item\",\"remark\":\"条目\"}}]}", "路线配置包含重复条目，请删除后重新添加")]
    public void CollectionStorageErrorsUseDisplayNamesAndActionableMessages(string content, string message)
    {
        const string schema = """[{"name":"rows","label":"路线配置","type":"collection","mode":"dynamic","item":{"fields":[{"name":"value","type":"text"}]}}]""";
        var exception = Assert.Throws<InvalidOperationException>(() => PluginSettingsSchema.ValidateStorage(schema, content));
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
        PluginSettingsSchema.ValidateStorage(Definition, saved.ToJsonString());
    }

    [Fact]
    public void DiscoveryInitializesMissingConfigurationButExistingFileIsOnlyFilledOnSave()
    {
        var paths = PortableDataPaths.Create(root);
        var directory = Path.Combine(paths.PluginsDirectory, "test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            """{"id":"test","name":"Test","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
        File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
        File.WriteAllText(Path.Combine(directory, "settings.json"), Definition);
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, new Runtime(), pages);
        var data = Path.Combine(paths.PluginDataDirectory, "test");
        var path = Path.Combine(data, "config.json");
        Assert.True(File.Exists(Path.Combine(data, "plugin.json")));
        Assert.True(JsonNode.Parse(File.ReadAllText(path))!["toggle"]!.GetValue<bool>());
        const string partial = """{"text":"原值","toggle":false}""";
        File.WriteAllText(path, partial);
        manager.Reload();
        var loaded = JsonNode.Parse(manager.ReadStorage("test")!)!;
        Assert.Equal("原值", loaded["text"]!.GetValue<string>());
        Assert.Equal(0, loaded["count"]!.GetValue<int>());
        Assert.Equal(partial, File.ReadAllText(path));
        manager.WriteStorage("test", partial);
        var written = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("原值", written["text"]!.GetValue<string>());
        Assert.False(written["toggle"]!.GetValue<bool>());
        Assert.Equal(0, written["count"]!.GetValue<int>());
        Assert.False(File.Exists(path + ".tmp"));
        manager.WriteStorage("test", """{"count":""}""");
        Assert.Equal(0, JsonNode.Parse(File.ReadAllText(path))!["count"]!.GetValue<int>());
        var original = File.ReadAllText(path);
        Assert.Throws<InvalidOperationException>(() => manager.WriteStorage("test", """{"toggle":"true"}"""));
        Assert.Equal(original, File.ReadAllText(path));
        foreach (var invalid in new[] { "null", "[]", "1" })
        {
            Assert.Throws<InvalidOperationException>(() => manager.WriteStorage("test", invalid));
            Assert.Equal(original, File.ReadAllText(path));
        }
    }

    [Fact]
    public async Task UpdatesReadLatestContentAndLeaveNoTemporaryFile()
    {
        var path = Path.Combine(root, "config.json");
        Assert.Null(configurationStore.Read(path));
        Assert.False(Directory.Exists(root));
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            configurationStore.Update(path, current =>
            {
                current["count"] = (current["count"]?.GetValue<int>() ?? 0) + 1;
                return current;
            }))));
        Assert.Equal(100, configurationStore.Read(path)!["count"]!.GetValue<int>());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    public void InvalidExistingConfigurationIsNeverOverwritten(string content)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.json");
        File.WriteAllText(path, content);
        Assert.Throws<InvalidDataException>(() => configurationStore.Update(path, _ => new JsonObject()));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void FailedUpdateLeavesOriginalFileIntact()
    {
        var path = Path.Combine(root, "config.json");
        configurationStore.Update(path, _ => new JsonObject { ["value"] = 7 });
        var original = File.ReadAllText(path);
        Assert.Throws<InvalidOperationException>(() => configurationStore.Update(path, _ => throw new InvalidOperationException()));
        Assert.Equal(original, File.ReadAllText(path));
        Directory.CreateDirectory(path + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => configurationStore.Update(path, _ => new JsonObject()));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task DisplayAndEnableUpdatesPreserveOtherSections()
    {
        var paths = PortableDataPaths.Create(root);
        var directory = Path.Combine(paths.PluginsDirectory, "test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            """{"id":"test","name":"Test","version":"1.0.0","main":"plugin.dll"}""");
        File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, new Runtime(), pages);
        var path = Path.Combine(paths.PluginDataDirectory, "test", "plugin.json");
        configurationStore.Update(path, _ => new JsonObject { ["enabled"] = false, ["extra"] = 123 });
        await Task.WhenAll(Enumerable.Range(0, 80).Select(index => Task.Run(() =>
        {
            switch (index % 4)
            {
                case 0: manager.WriteStatusDisplaySettings("test", new(false, [], "状态")); break;
                case 1: manager.WriteFeatureDisplaySettings("test", new(false, [], "功能")); break;
                case 2: manager.WriteSettingsDisplaySettings("test", new(false)); break;
                default: manager.Enable("test"); break;
            }
        })));
        var saved = configurationStore.Read(path)!;
        Assert.True(saved["enabled"]!.GetValue<bool>());
        Assert.Equal("状态", saved["statusDisplay"]!["homeTitle"]!.GetValue<string>());
        Assert.Equal("功能", saved["featureDisplay"]!["homeTitle"]!.GetValue<string>());
        Assert.False(saved["settingsDisplay"]!["registerOnHome"]!.GetValue<bool>());
        Assert.Equal(123, saved["extra"]!.GetValue<int>());
        Assert.False(File.Exists(path + ".tmp"));
        var original = File.ReadAllText(path);
        File.WriteAllText(path, "{");
        Assert.Throws<InvalidDataException>(() => manager.WriteSettingsDisplaySettings("test", new(true)));
        Assert.Equal("{", File.ReadAllText(path));
        File.WriteAllText(path, original);
        manager.Disable("test");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ConcurrentDeleteAndSavesNeverRecreateDeletedPluginData()
    {
        var paths = PortableDataPaths.Create(root);
        var directory = Path.Combine(paths.PluginsDirectory, "test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            """{"id":"test","name":"Test","version":"1.0.0","main":"plugin.dll"}""");
        File.WriteAllText(Path.Combine(directory, "plugin.dll"), "");
        using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
        var manager = new PluginManager(paths, NullLogger<PluginManager>.Instance, new Runtime(), pages);
        using var start = new ManualResetEventSlim();
        var operations = Enumerable.Range(0, 41).Select(index => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                switch (index % 4)
                {
                    case 0 when index == 0: manager.Delete("test", true); break;
                    case 0: manager.WriteStorage("test", "{}"); break;
                    case 1: manager.WriteStatusDisplaySettings("test", new(false)); break;
                    case 2: manager.WriteFeatureDisplaySettings("test", new(false)); break;
                    default: manager.WriteSettingsDisplaySettings("test", new(false)); break;
                }
            }
            catch (FileNotFoundException) { }
        })).ToArray();
        start.Set();
        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(manager.GetPlugins());
        Assert.False(Directory.Exists(directory));
        Assert.False(Directory.Exists(Path.Combine(paths.PluginDataDirectory, "test")));
    }

    private sealed class Runtime : IPluginRuntime
    {
        public IPluginRuntimeInstance Start(PluginInfo plugin) => new Instance();
        private sealed class Instance : IPluginRuntimeInstance
        {
            public void Dispose() { }
        }
    }
}
