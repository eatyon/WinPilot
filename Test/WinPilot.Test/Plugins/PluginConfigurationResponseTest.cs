using System.Text.Json.Nodes;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class PluginConfigurationResponseTest
{
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
        const string storage = """
            {"secret":"private","plain":"visible","fixed":{"account":{"token":"private-token"}},"dynamic":[{"password":"private-password","user":"name","__winpilot":{"id":"1","remark":"account"}},{"user":"other"}]}
            """;
        var result = PluginConfigurationResponse.MaskSensitiveValues(definition, storage);
        var filtered = JsonNode.Parse(result.Storage)!;
        Assert.Equal(new string('•', "private".Length), filtered["secret"]!.GetValue<string>());
        Assert.Equal("visible", filtered["plain"]!.GetValue<string>());
        Assert.Equal(new string('•', "private-token".Length), filtered["fixed"]!["account"]!["token"]!.GetValue<string>());
        Assert.Equal(new string('•', "private-password".Length), filtered["dynamic"]![0]!["password"]!.GetValue<string>());
        Assert.Equal("", filtered["dynamic"]![1]!["password"]!.GetValue<string>());
        Assert.Equal("name", filtered["dynamic"]![0]!["user"]!.GetValue<string>());
        Assert.Equal("1", filtered["dynamic"]![0]!["__winpilot"]!["id"]!.GetValue<string>());
        Assert.Equal(2, filtered["dynamic"]!.AsArray().Count);
        Assert.DoesNotContain("private", result.Storage);
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
        var storage = new JsonObject { ["secret"] = secret }.ToJsonString();
        var result = PluginConfigurationResponse.MaskSensitiveValues(
            """[{"name":"secret","type":"text","masked":true}]""", storage);
        var masked = JsonNode.Parse(result.Storage)!["secret"]!.GetValue<string>();
        Assert.Equal(new string('•', secret.Length), masked);
    }

    [Fact]
    public void PreservesReadableUnicodeAndRequiredJsonEscapes()
    {
        const string definition = """[{"name":"secret","type":"text","masked":true,"label":"隐私项"},{"name":"plain","type":"text"}]""";
        const string storage = """{"secret":"私密内容","plain":"中文\"路径\\目录\n下一行"}""";
        var result = PluginConfigurationResponse.MaskSensitiveValues(definition, storage);
        Assert.Contains("隐私项", result.Definition);
        Assert.Contains("••••", result.Storage);
        Assert.Contains("中文\\\"路径\\\\目录\\n下一行", result.Storage);
        Assert.Equal("中文\"路径\\目录\n下一行", JsonNode.Parse(result.Storage)!["plain"]!.GetValue<string>());
        Assert.DoesNotContain("私密内容", result.Storage);
    }
}
