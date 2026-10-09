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
            builder.Services.AddSingleton<PluginTaskExtensionRegistry>();
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
            builder.Services.AddSingleton<PluginTaskExtensionRegistry>();
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
            builder.Services.AddSingleton<PluginTaskExtensionRegistry>();
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
            builder.Services.AddSingleton<PluginTaskExtensionRegistry>();
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
