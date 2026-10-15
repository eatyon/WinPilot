using System.Text.Json.Nodes;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class PluginConfigurationResponseTest
{
    [Fact]
    public void DeletedDeclarationsAndUnknownFieldsAreExcludedAtEveryLevel()
    {
        const string definition = """
            [{"name":"plain","type":"text"},
             {"name":"fixed","type":"collection","mode":"fixed","items":[{"name":"account","fields":[{"name":"user","type":"text"}]}]},
             {"name":"dynamic","type":"collection","mode":"dynamic","item":{"fields":[{"name":"user","type":"text"}]}}]
            """;
        const string configuration = """
            {"plain":"visible","deletedSecret":"private-old",
             "fixed":{"account":{"user":"fixed-user","deletedSecret":"private-fixed"},"removed":{"token":"private-removed"}},
             "dynamic":[{"user":"dynamic-user","deletedSecret":"private-dynamic","__winpilot":{"id":"one","remark":"account","token":"private-metadata"}}]}
            """;
        var result = PluginConfigurationResponse.MaskSensitiveValues(definition, configuration);
        var projected = JsonNode.Parse(result.Configuration)!;
        Assert.Equal("visible", projected["plain"]!.GetValue<string>());
        Assert.Equal("fixed-user", projected["fixed"]!["account"]!["user"]!.GetValue<string>());
        Assert.Equal("dynamic-user", projected["dynamic"]![0]!["user"]!.GetValue<string>());
        Assert.Equal("one", projected["dynamic"]![0]!["__winpilot"]!["id"]!.GetValue<string>());
        Assert.Equal("account", projected["dynamic"]![0]!["__winpilot"]!["remark"]!.GetValue<string>());
        Assert.False(projected.AsObject().ContainsKey("deletedSecret"));
        Assert.False(projected["fixed"]!.AsObject().ContainsKey("removed"));
        Assert.DoesNotContain("private-", result.Configuration);
        Assert.DoesNotContain("deletedSecret", result.Configuration);
    }

    [Theory]
    [InlineData("dynamic", "{\"token\":\"private-value\"}")]
    [InlineData("dynamic", "\"private-value\"")]
    [InlineData("dynamic", "null")]
    [InlineData("fixed", "[{\"token\":\"private-value\"}]")]
    [InlineData("fixed", "\"private-value\"")]
    [InlineData("fixed", "null")]
    public void MismatchedCollectionStructuresAreExcluded(string mode, string value)
    {
        var definition = mode == "dynamic"
            ? """[{"name":"accounts","type":"collection","mode":"dynamic","item":{"fields":[{"name":"token","type":"text","masked":true,"default":"private-default"}]}}]"""
            : """[{"name":"accounts","type":"collection","mode":"fixed","items":[{"name":"one","fields":[{"name":"token","type":"text","masked":true,"default":"private-default"}]}]}]""";
        var result = PluginConfigurationResponse.MaskSensitiveValues(definition, "{\"accounts\":" + value + "}");
        Assert.Empty(JsonNode.Parse(result.Configuration)!.AsObject());
        Assert.DoesNotContain("private-", result.Configuration);
        Assert.DoesNotContain("private-default", result.Definition);
    }

    [Fact]
    public void InvalidCollectionItemsAndMetadataAreNotForwarded()
    {
        const string definition = """
            [{"name":"fixed","type":"collection","mode":"fixed","items":[{"name":"one","fields":[{"name":"plain","type":"text"}]}]},
             {"name":"dynamic","type":"collection","mode":"dynamic","item":{"fields":[{"name":"plain","type":"text"}]}}]
            """;
        const string configuration = """
            {"fixed":{"one":[{"token":"private-fixed"}]},
             "dynamic":["private-string",["private-array"],null,123,{"plain":"visible","__winpilot":{"id":{"token":"private-id"},"remark":["private-remark"]}}]}
            """;
        var result = PluginConfigurationResponse.MaskSensitiveValues(definition, configuration);
        var projected = JsonNode.Parse(result.Configuration)!;
        Assert.Empty(projected["fixed"]!.AsObject());
        var item = Assert.Single(projected["dynamic"]!.AsArray())!.AsObject();
        Assert.Equal("visible", item["plain"]!.GetValue<string>());
        Assert.DoesNotContain("private-", result.Configuration);
    }

    [Theory]
    [InlineData("text", "{\"token\":\"private-value\"}")]
    [InlineData("text", "null")]
    [InlineData("textarea", "[]")]
    [InlineData("select", "{}")]
    [InlineData("number", "\"private-value\"")]
    [InlineData("checkbox", "{}")]
    [InlineData("multi-checkbox", "[\"visible\",{\"token\":\"private-value\"}]")]
    public void InvalidOrdinaryFieldStructuresAreExcluded(string type, string value)
    {
        var definition = $$"""[{"name":"field","type":"{{type}}"}]""";
        var result = PluginConfigurationResponse.MaskSensitiveValues(definition, "{\"field\":" + value + "}");
        Assert.Empty(JsonNode.Parse(result.Configuration)!.AsObject());
    }

    [Fact]
    public void ValidOrdinaryFieldTypesKeepTheirValues()
    {
        const string definition = """
            [{"name":"text","type":"text"},{"name":"textarea","type":"textarea"},{"name":"select","type":"select"},
             {"name":"number","type":"number"},{"name":"checkbox","type":"checkbox"},{"name":"multi","type":"multi-checkbox"}]
            """;
        const string configuration = """{"text":"中文","textarea":"line","select":"choice","number":1.5,"checkbox":false,"multi":["one","two"]}""";
        var result = PluginConfigurationResponse.MaskSensitiveValues(definition, configuration);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(configuration), JsonNode.Parse(result.Configuration)));
    }

    [Fact]
    public void MasksSensitiveValuesAndRemovesDefaultsWhilePreservingOtherFieldsAndCollections()
    {
        const string definition = """
            [
              {"name":"secret","type":"text","masked":true,"default":"secret-default"},
              {"name":"plain","type":"text","masked":false,"default":"ordinary"},
              {"name":"fixed","type":"collection","mode":"fixed","items":[{"name":"account","fields":[{"name":"token","type":"text","masked":true,"default":"token-default"}]}]},
              {"name":"dynamic","type":"collection","mode":"dynamic","item":{"fields":[{"name":"password","type":"text","masked":true,"default":"password-default"},{"name":"user","type":"text"}]}}
            ]
            """;
        const string configuration = """
            {"secret":"private","plain":"visible","fixed":{"account":{"token":"private-token"}},"dynamic":[{"password":"private-password","user":"name","__winpilot":{"id":"1","remark":"account"}},{"user":"other"}]}
            """;
        var result = PluginConfigurationResponse.MaskSensitiveValues(definition, configuration);
        var filtered = JsonNode.Parse(result.Configuration)!;
        Assert.Equal(new string('•', "private".Length), filtered["secret"]!.GetValue<string>());
        Assert.Equal("visible", filtered["plain"]!.GetValue<string>());
        Assert.Equal(new string('•', "private-token".Length), filtered["fixed"]!["account"]!["token"]!.GetValue<string>());
        Assert.Equal(new string('•', "private-password".Length), filtered["dynamic"]![0]!["password"]!.GetValue<string>());
        Assert.Equal("", filtered["dynamic"]![1]!["password"]!.GetValue<string>());
        Assert.Equal("name", filtered["dynamic"]![0]!["user"]!.GetValue<string>());
        Assert.Equal("1", filtered["dynamic"]![0]!["__winpilot"]!["id"]!.GetValue<string>());
        Assert.Equal(2, filtered["dynamic"]!.AsArray().Count);
        Assert.DoesNotContain("private", result.Configuration);
        Assert.DoesNotContain("secret-default", result.Definition);
        Assert.DoesNotContain("token-default", result.Definition);
        Assert.DoesNotContain("password-default", result.Definition);
        Assert.Contains("ordinary", result.Definition);
    }

    [Theory]
    [InlineData("")]
    [InlineData("中文内容")]
    [InlineData("token-123")]
    public void MaskedValueRetainsItsLength(string secret)
    {
        var configuration = new JsonObject { ["secret"] = secret }.ToJsonString();
        var result = PluginConfigurationResponse.MaskSensitiveValues(
            """[{"name":"secret","type":"text","masked":true}]""", configuration);
        var masked = JsonNode.Parse(result.Configuration)!["secret"]!.GetValue<string>();
        Assert.Equal(new string('•', secret.Length), masked);
    }

    [Fact]
    public void PreservesReadableUnicodeAndRequiredJsonEscapes()
    {
        const string definition = """[{"name":"secret","type":"text","masked":true,"label":"隐私项"},{"name":"plain","type":"text"}]""";
        const string configuration = """{"secret":"私密内容","plain":"中文\"路径\\目录\n下一行"}""";
        var result = PluginConfigurationResponse.MaskSensitiveValues(definition, configuration);
        Assert.Contains("隐私项", result.Definition);
        Assert.Contains("••••", result.Configuration);
        Assert.Contains("中文\\\"路径\\\\目录\\n下一行", result.Configuration);
        Assert.Equal("中文\"路径\\目录\n下一行", JsonNode.Parse(result.Configuration)!["plain"]!.GetValue<string>());
        Assert.DoesNotContain("私密内容", result.Configuration);
    }
}
