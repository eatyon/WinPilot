using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Features.Plugins;
using WinPilot.Web;

using static WinPilot.Test.PluginTestSupport;

namespace WinPilot.Test;

public sealed class PluginEndpointTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationSaveResponseMatchesDefaultedSnapshot(bool emptyNumber)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var pluginDirectory = Path.Combine(paths.PluginsDirectory, "example");
            File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"),
                """{"id":"example","name":"Example","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
            File.WriteAllText(Path.Combine(pluginDirectory, "settings.json"), PluginConfigurationTestData.Definition);
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var request = new StringContent(emptyNumber ? """{"count":""}""" : "{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PutAsync("/api/plugins/example/configuration", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var snapshot = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            var path = Path.Combine(paths.PluginDataDirectory, "example", "config.json");
            var saved = File.ReadAllText(path);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(saved), snapshot));
            Assert.Equal(0, snapshot["count"]!.GetValue<int>());
            Assert.True(snapshot["toggle"]!.GetValue<bool>());
            Assert.Equal("", snapshot["text"]!.GetValue<string>());
            Assert.Empty(snapshot["choices"]!.AsArray());
            using var rejected = await client.PutAsJsonAsync("/api/plugins/example/configuration", new { count = "invalid" });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(saved, File.ReadAllText(path));
            using var failure = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.True(failure.RootElement.TryGetProperty("detail", out _));
            Assert.False(failure.RootElement.TryGetProperty("count", out _));
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverallScanFailureReturnsFixedServerProblemAndCanRecover(bool startupFailure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            if (startupFailure)
            {
                Directory.CreateDirectory(paths.DataDirectory);
                File.WriteAllText(paths.PluginsDirectory, "blocked");
            }
            else WritePlugin(paths, "example", "example", "");
            using var logs = new WinPilot.Infrastructure.RollingFileLoggerProvider(paths.LogsDirectory);
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
            var runtime = new RecordingPluginRuntime();
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages, loggerFactory.CreateLogger<PluginManager>());
            var original = manager.GetPlugins();
            if (!startupFailure)
            {
                Directory.Move(paths.PluginsDirectory, paths.PluginsDirectory + "-unavailable");
                File.WriteAllText(paths.PluginsDirectory, "blocked");
            }
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var failed = await client.PostAsync("/api/plugins/scan", null);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal("application/problem+json", failed.Content.Headers.ContentType?.MediaType);
            var body = await failed.Content.ReadAsStringAsync();
            using var problem = JsonDocument.Parse(body);
            Assert.Equal("插件扫描失败", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal("无法扫描插件目录，请检查目录权限和程序日志", problem.RootElement.GetProperty("detail").GetString());
            Assert.DoesNotContain(directory, body);
            Assert.DoesNotContain("IOException", body);
            Assert.Equal(original, manager.GetPlugins());
            var log = File.ReadAllText(Directory.GetFiles(paths.LogsDirectory, "winpilot*.log").Single());
            Assert.Contains("IOException", log);
            Assert.Equal(startupFailure ? 2 : 1, log.Split("扫描插件目录失败", StringSplitOptions.None).Length - 1);
            File.Delete(paths.PluginsDirectory);
            if (startupFailure) WritePlugin(paths, "example", "example", "");
            else Directory.Move(paths.PluginsDirectory + "-unavailable", paths.PluginsDirectory);
            using var recovered = await client.PostAsync("/api/plugins/scan", null);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            using var recoveredJson = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync());
            Assert.Equal("example", Assert.Single(recoveredJson.RootElement.EnumerateArray()).GetProperty("id").GetString());
            Assert.Equal(new[] { "example" }, runtime.StartedIds);
            Assert.Empty(runtime.DisposedIds);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public async Task ScanWithIndividualRuntimeFailureKeepsSuccessfulResponseAndFailedPluginState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            using var runtime = new WaitingPluginRuntime(true) { BlockStart = true };
            runtime.Release();
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, runtime, pages);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var scanned = await client.PostAsync("/api/plugins/scan", null);
            Assert.Equal(HttpStatusCode.OK, scanned.StatusCode);
            Assert.Equal(WinPilot.Core.Plugins.PluginState.Failed, manager.GetPlugin("example").State);
            using var enabled = await client.PostAsync("/api/plugins/example/enable", null);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            using var json = JsonDocument.Parse(await enabled.Content.ReadAsStringAsync());
            Assert.Equal("Failed", json.RootElement.GetProperty("state").GetString());
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("oversized")]
    [InlineData("invalid-utf8")]
    [InlineData("missing")]
    public async Task ReadmeHeadChecksExistenceOnlyAndSharesSuccessfulContentTypeWithGet(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var readme = Path.Combine(paths.PluginsDirectory, "example", "README.md");
            if (kind == "valid") File.WriteAllText(readme, "说明文档", new System.Text.UTF8Encoding(true));
            else if (kind == "oversized") File.WriteAllBytes(readme, new byte[512 * 1024 + 1]);
            else if (kind == "invalid-utf8") File.WriteAllBytes(readme, [0xff, 0xfe, 0xff]);
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Head, "/api/plugins/example/readme");
            using var head = await client.SendAsync(request);
            Assert.Equal(kind == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.OK, head.StatusCode);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            if (kind != "missing") Assert.Equal("text/plain; charset=utf-8", head.Content.Headers.ContentType?.ToString());
            if (kind == "valid")
            {
                using var get = await client.GetAsync("/api/plugins/example/readme");
                Assert.Equal(HttpStatusCode.OK, get.StatusCode);
                Assert.Equal(head.Content.Headers.ContentType?.ToString(), get.Content.Headers.ContentType?.ToString());
                Assert.Equal("说明文档", await get.Content.ReadAsStringAsync());
                Assert.NotEqual(new FileInfo(readme).Length, head.Content.Headers.ContentLength);
            }
            else if (kind is "oversized" or "missing")
            {
                using var get = await client.GetAsync("/api/plugins/example/readme");
                Assert.Equal(kind == "oversized" ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, get.StatusCode);
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebLogPreservesSuccessAndReturnsChineseDetailWithDisabledCode(bool enabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
            if (!enabled) manager.Disable("example");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var response = await client.PostAsJsonAsync("/api/plugins/example/web/log", new { level = "info", message = "test" });
            Assert.Equal(enabled ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (enabled) Assert.True(json.RootElement.GetProperty("accepted").GetBoolean());
            else
            {
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
                Assert.Equal("插件操作失败", json.RootElement.GetProperty("title").GetString());
                Assert.Equal("插件已禁用", json.RootElement.GetProperty("detail").GetString());
                Assert.Equal("plugin_disabled", json.RootElement.GetProperty("code").GetString());
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(" ", "/api/plugins/{id}")]
    [InlineData("   ", "/api/plugins/{id}")]
    [InlineData(" ", "/api/plugins/{id}?waitForLifecycle=true")]
    [InlineData("   ", "/api/plugins/{id}?waitForLifecycle=true")]
    [InlineData(" ", "/api/plugins/{id}/events")]
    [InlineData("   ", "/api/plugins/{id}/events")]
    [InlineData(" ", "/plugin-assets/{id}/index.js")]
    [InlineData("   ", "/plugin-assets/{id}/index.js")]
    [InlineData(" ", "/plugin-assets/{id}/v/test/index.js")]
    [InlineData("   ", "/plugin-assets/{id}/v/test/index.js")]
    public async Task BlankPluginIdsReturnProblemBeforeStartingResponses(string id, string route)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(PortableDataPaths.Create(directory), new RecordingPluginRuntime(), pages);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var response = await client.GetAsync(route.Replace("{id}", Uri.EscapeDataString(id)));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("插件操作失败", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal("插件 ID 不能为空", problem.RootElement.GetProperty("detail").GetString());
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public async Task SafeConfigurationReadsHideSecretsAndRestoreThemWithoutChangingConfiguration()
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
            var configurationPath = Path.Combine(paths.PluginDataDirectory, id, "config.json");
            const string original = """{"secret":"private-value","plain":"中文内容"}""";
            File.WriteAllText(configurationPath, original);
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
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
            Assert.Equal(original, File.ReadAllText(configurationPath));
            safeMode.SetEnabled(false);
            Assert.Contains("private-value", await client.GetStringAsync(route + "/configuration"));
            Assert.Contains("private-default", await client.GetStringAsync(route + "/configuration/schema"));
            manager.Disable(id);
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("collection")]
    public async Task SafeConfigurationProjectionExcludesUnrecognizedDataWithoutChangingOrdinaryReadsOrFiles(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            WritePlugin(paths, "example", "example", "");
            var pluginDirectory = Path.Combine(paths.PluginsDirectory, "example");
            var definitionPath = Path.Combine(pluginDirectory, "settings.json");
            File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"),
                """{"id":"example","name":"Example","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json"}""");
            File.WriteAllText(definitionPath, scenario == "removed"
                ? """[{"name":"secret","type":"text","masked":true}]"""
                : """[{"name":"accounts","type":"collection","mode":"dynamic","item":{"fields":[{"name":"secret","type":"text","masked":true,"default":"private-default"}]}}]""");
            var configurationPath = Path.Combine(paths.PluginDataDirectory, "example", "config.json");
            var original = scenario == "removed" ? """{"secret":"private-old"}""" : """{"accounts":{"secret":"private-collection"}}""";
            File.WriteAllText(configurationPath, original);
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<WinPilot.Features.Terminal.TerminalSessionManager>();
            builder.Services.AddSingleton<WinPilot.Features.Security.SafeModeService>();
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(new PluginWebResourceService(manager));
            await using var app = builder.Build();
            app.UseMiddleware<SafeModeMiddleware>();
            app.MapPluginEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var safeMode = app.Services.GetRequiredService<WinPilot.Features.Security.SafeModeService>();
            const string route = "/api/plugins/example/configuration";
            safeMode.SetEnabled(true);
            if (scenario == "removed")
            {
                var masked = JsonNode.Parse(await client.GetStringAsync(route))!;
                Assert.Equal(new string('•', "private-old".Length), masked["secret"]!.GetValue<string>());
                File.WriteAllText(definitionPath, "[]");
            }
            var declaration = File.ReadAllText(definitionPath);
            using var safeResponse = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, safeResponse.StatusCode);
            Assert.True(safeResponse.Headers.CacheControl?.NoStore);
            var safeContent = await safeResponse.Content.ReadAsStringAsync();
            Assert.Empty(JsonNode.Parse(safeContent)!.AsObject());
            Assert.DoesNotContain("private-", safeContent);
            var safeSchema = await client.GetStringAsync(route + "/schema");
            Assert.DoesNotContain("private-", safeSchema);
            Assert.Equal(original, File.ReadAllText(configurationPath));
            Assert.Equal(declaration, File.ReadAllText(definitionPath));
            safeMode.SetEnabled(false);
            var ordinary = JsonNode.Parse(await client.GetStringAsync(route));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), ordinary));
            Assert.Equal(original, File.ReadAllText(configurationPath));
            if (scenario == "collection") Assert.Contains("private-default", await client.GetStringAsync(route + "/schema"));
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
        PluginPageSessionManager? pages = null;
        try
        {
            var paths = PortableDataPaths.Create(applicationDirectory);
            WritePlugin(paths, "example", "example", "");
            pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            manager = CreatePluginManager(paths, runtime, pages);
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
            try
            {
                runtime.Release();
                if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(5));
                manager?.Disable("example");
            }
            finally
            {
                try { manager?.Dispose(); }
                finally
                {
                    try { pages?.Dispose(); }
                    finally { TryDeleteDirectory(applicationDirectory); }
                }
            }
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
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
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
    [InlineData(1, false, HttpStatusCode.OK, "{\"offset\":0}", "APPLICATION/OCTET-STREAM")]
    [InlineData(1, false, HttpStatusCode.OK, "{\"offset\":0}", "application/octet-stream; charset=binary")]
    [InlineData(1, false, HttpStatusCode.UnsupportedMediaType, "{}", "TEXT/PLAIN; charset=utf-8")]
    [InlineData(1, false, HttpStatusCode.UnsupportedMediaType, "{}", "application/octet-stream; charset=\"")]
    [InlineData(1, false, HttpStatusCode.UnsupportedMediaType, "{}", null)]
    [InlineData(1, false, HttpStatusCode.InternalServerError, "{}", "application/octet-stream", false)]
    [InlineData(1, false, HttpStatusCode.NotFound, "{}", "application/octet-stream", true, true)]
    public async Task PageUploads_TransferBinaryWithBoundedSize(int size, bool unknownLength, HttpStatusCode expected,
        string parameters = "{\"offset\":0}", string? contentType = "application/octet-stream", bool registerHandler = true, bool closeSession = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Test", Guid.NewGuid().ToString("N"));
        try
        {
            using var sessions = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = new PluginManager(PortableDataPaths.Create(directory),
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
            if (contentType is not null) Assert.True(content.Headers.TryAddWithoutValidation("Content-Type", contentType));
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
            if (expected == HttpStatusCode.UnsupportedMediaType) Assert.Empty(await response.Content.ReadAsByteArrayAsync());
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
            using var pages = new PluginPageSessionManager(NullLogger<PluginPageSessionManager>.Instance);
            using var manager = CreatePluginManager(paths, new RecordingPluginRuntime(), pages);
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
