using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Features.Updates;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class UpdateSettingsEndpointTest
{
    [Fact]
    public async Task SettingsEndpointsPersistAndSafeModeBlocksOnlyChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "winpilot-update-api", Guid.NewGuid().ToString("N"));
        var paths = PortableDataPaths.Create(directory);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<TerminalSessionManager>();
            builder.Services.AddSingleton<SafeModeService>();
            builder.Services.AddSingleton(new UpdateCheckService(NullLogger<UpdateCheckService>.Instance, new HttpClient(new Handler()), paths.ConfigurationFile, "1.0.0"));
            await using var app = builder.Build();
            app.UseMiddleware<SafeModeMiddleware>();
            app.MapHealthEndpoint();
            app.MapUpdateEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var initial = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/update/settings");
            Assert.Equal("GitHub", initial!["source"]);
            Assert.Equal("Stable", initial["channel"]);
            using var rejectedInstall = await client.PostAsJsonAsync("/api/update/install", new { version = "2.0.0", source = "GitHub", channel = "Stable" });
            Assert.Equal(HttpStatusCode.Conflict, rejectedInstall.StatusCode);
            var installError = await rejectedInstall.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal("更新信息已变化，请重新检查更新", installError.GetProperty("message").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/update/settings", new { source = "Cnb", channel = "Preview" })).StatusCode);
            Assert.Equal(UpdateSource.Cnb, WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).Updates.Source);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/update/settings", new { source = 99, channel = "Stable" })).StatusCode);
            app.Services.GetRequiredService<SafeModeService>().SetEnabled(true);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/update/settings", new { source = "GitHub", channel = "Stable" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/update/settings")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/update/status")).StatusCode);
            var result = await client.PostAsync("/api/update/check", null);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var payload = await result.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.True(payload.GetProperty("checkFailed").GetBoolean());
            Assert.Equal("Cnb", payload.GetProperty("source").GetString());
            Assert.Equal("Preview", payload.GetProperty("channel").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
