using WinPilot.Core;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Features.Plugins;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed partial class PluginManagerTest
{
    [Fact]
    public async Task SafeConfigurationReadsHideSecretsAndRestoreThemWithoutChangingStorage()
    {
        const string id = "example";
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, id, id, "");
            var pluginDirectory = Path.Combine(paths.PluginsDirectory, id);
            File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"),
                $$"""{"id":"{{id}}","name":"Example","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
            File.WriteAllText(Path.Combine(pluginDirectory, "settings.json"),
                """[{"name":"secret","type":"text","masked":true,"default":"private-default"},{"name":"plain","type":"text","label":"中文配置"}]""");
            var storagePath = Path.Combine(paths.PluginDataDirectory, id, "config.json");
            const string original = """{"secret":"private-value","plain":"中文内容"}""";
            File.WriteAllText(storagePath, original);
            var manager = CreatePluginManager(paths, new RecordingPluginRuntime());
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<WinPilot.Features.Terminal.TerminalSessionManager>();
            builder.Services.AddSingleton<WinPilot.Features.Security.SafeModeService>();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var safeMode = app.Services.GetRequiredService<WinPilot.Features.Security.SafeModeService>();
            var route = $"/api/plugins/{id}";
            Assert.Contains("all-enabled", await client.GetStringAsync("/api/plugins/options/settings/notificationChannels"));
            Assert.Contains("private-value", await client.GetStringAsync(route + "/configuration"));
            safeMode.SetEnabled(true);
            foreach (var path in new[] { "configuration", "configuration/schema" })
            {
                using var response = await client.GetAsync($"{route}/{path}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                var content = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("private-", content);
                Assert.Contains(path == "configuration" ? "中文内容" : "中文配置", content);
                Assert.DoesNotContain("\\u", content);
                if (path == "configuration") Assert.Contains(new string('•', "private-value".Length), content);
            }
            Assert.Contains("中文内容", await client.GetStringAsync(route + "/configuration"));
            Assert.Equal(original, File.ReadAllText(storagePath));
            safeMode.SetEnabled(false);
            Assert.Contains("private-value", await client.GetStringAsync(route + "/configuration"));
            Assert.Contains("private-default", await client.GetStringAsync(route + "/configuration/schema"));
            manager.Disable(id);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PluginQuery_WaitForLifecycleReturnsFinalStateAndOrdinaryQueryRemainsImmediate(bool failStart)
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        using var runtime = new WaitingPluginRuntime(failStart);
        Task? operation = null;
        PluginManager? manager = null;
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            WritePlugin(paths, "example", "example", "");
            manager = CreatePluginManager(paths, runtime);
            manager.Disable("example");
            runtime.BlockStart = true;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            operation = Task.Run(() => manager.Enable("example"));
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var immediate = await client.GetAsync("/api/plugins/example").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, immediate.StatusCode);
            using var initial = System.Text.Json.JsonDocument.Parse(await immediate.Content.ReadAsStringAsync());
            Assert.Equal((int)WinPilot.Core.Plugins.PluginState.Loaded, initial.RootElement.GetProperty("state").GetInt32());
            var waiting = client.GetAsync("/api/plugins/example?waitForLifecycle=true");
            Assert.False(waiting.IsCompleted);
            runtime.Release();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            using var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            using var final = System.Text.Json.JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            Assert.Equal((int)(failStart ? WinPilot.Core.Plugins.PluginState.Failed : WinPilot.Core.Plugins.PluginState.Running), final.RootElement.GetProperty("state").GetInt32());
            using var missing = await client.GetAsync("/api/plugins/missing?waitForLifecycle=true");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            runtime.Release();
            if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(5));
            manager?.Disable("example");
            TryDeleteDirectory(applicationDirectory);
        }
    }

    [Theory]
    [InlineData(64 * 1024, false, HttpStatusCode.NotFound)]
    [InlineData(64 * 1024, true, HttpStatusCode.NotFound)]
    [InlineData(64 * 1024 + 1, false, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(64 * 1024 + 1, true, HttpStatusCode.RequestEntityTooLarge)]
    public async Task PageMessages_EnforceByteLimitWithAndWithoutContentLength(int size, bool unknownLength, HttpStatusCode expected)
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var manager = CreatePluginManager(paths, new RecordingPluginRuntime());
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var bytes = new byte[size];
            using HttpContent content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            using var response = await client.PostAsync("/api/plugin-page-sessions/missing/messages", content);
            Assert.Equal(expected, response.StatusCode);
        }
        finally { TryDeleteDirectory(applicationDirectory); }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Theory]
    [InlineData(512 * 1024, false, HttpStatusCode.OK)]
    [InlineData(1024 * 1024, true, HttpStatusCode.OK)]
    [InlineData(1024 * 1024 + 1, false, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(1024 * 1024 + 1, true, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(0, false, HttpStatusCode.BadRequest)]
    [InlineData(1, false, HttpStatusCode.BadRequest, "[]")]
    [InlineData(1, false, HttpStatusCode.BadRequest, "{")]
    [InlineData(1, false, HttpStatusCode.UnsupportedMediaType, "{}", "text/plain")]
    [InlineData(1, false, HttpStatusCode.BadRequest, "{}", "application/octet-stream", false)]
    [InlineData(1, false, HttpStatusCode.NotFound, "{}", "application/octet-stream", true, true)]
    public async Task PageUploads_TransferBinaryWithBoundedSize(int size, bool unknownLength, HttpStatusCode expected,
        string parameters = "{\"offset\":0}", string contentType = "application/octet-stream", bool registerHandler = true, bool closeSession = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            using var sessions = new PluginPageSessionManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginPageSessionManager>.Instance);
            var manager = new PluginManager(PortableDataPaths.Create(directory),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginManager>.Instance, new RecordingPluginRuntime(), sessions);
            WinPilot.PluginContract.PluginPageUpload? received = null;
            var id = sessions.Open("plugin", "page", session =>
            {
                if (registerHandler) session.RegisterUploadHandler((upload, _) =>
                {
                    received = upload;
                    return Task.FromResult("{\"succeeded\":true}");
                });
            });
            if (closeSession) sessions.Close(id);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var bytes = Enumerable.Repeat((byte)173, size).ToArray();
            using HttpContent content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            content.Headers.ContentType = new(contentType);
            using var response = await client.PostAsync($"/api/plugin-page-sessions/{id}/uploads?command=chunk&parameters={Uri.EscapeDataString(parameters)}", content);
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.OK)
            {
                Assert.NotNull(received);
                Assert.Equal("chunk", received.Command);
                Assert.Equal("{\"offset\":0}", received.ParametersJson);
                Assert.Equal(bytes, received.Content.ToArray());
            }
            else Assert.Null(received);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public async Task PageHeartbeat_MissingSessionReturnsNotFound()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            var manager = CreatePluginManager(paths, new RecordingPluginRuntime());
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var response = await client.PostAsync("/api/plugin-page-sessions/missing/heartbeat", null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            TryDeleteDirectory(applicationDirectory);
        }
    }
}
