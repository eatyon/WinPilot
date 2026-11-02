using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class ResponseJsonTest
{
    [Fact]
    public async Task HttpResponsePreservesWebNamingEnumsAndNullFields()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Encoder = ResponseJson.Default.Encoder;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        await using var app = builder.Build();
        app.MapGet("/json", () => Results.Ok(new
        {
            Message = "中文 <>&",
            Status = DayOfWeek.Monday,
            Optional = (string?)null
        }));
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/json");
        var json = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("中文 <>&", json);
        var value = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("Monday", value["status"]!.GetValue<string>());
        Assert.True(value.ContainsKey("optional"));
        Assert.Null(value["optional"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadableCharactersPreserveRequiredEscapingAndRoundTrip(bool web)
    {
        var options = web ? ResponseJson.Web : ResponseJson.Default;
        const string text = "中文 • <>&+'\"\\\r\n\t";
        var json = JsonSerializer.Serialize(new { Message = text }, options);

        Assert.Contains("中文 • <>&+", json);
        Assert.Contains("\\\"", json);
        Assert.Contains("\\\\", json);
        Assert.Contains("\\r\\n\\t", json);
        Assert.DoesNotContain('\n', json);
        Assert.Equal(text, JsonNode.Parse(json)![web ? "message" : "Message"]!.GetValue<string>());
    }

    [Fact]
    public void MaskedConfigurationUsesSharedEncodingWithoutExposingSecrets()
    {
        const string definition = """[{"name":"token","type":"text","masked":true,"default":"secret"},{"name":"name","type":"text"}]""";
        const string configuration = """{"token":"secret","name":"中文 <>&"}""";

        var response = PluginConfigurationResponse.MaskSensitiveValues(definition, configuration);

        Assert.DoesNotContain("secret", response.Definition);
        Assert.DoesNotContain("secret", response.Configuration);
        Assert.Contains("••••••", response.Configuration);
        Assert.Contains("中文 <>&", response.Configuration);
        Assert.Equal("••••••", JsonNode.Parse(response.Configuration)!["token"]!.GetValue<string>());
    }
}
