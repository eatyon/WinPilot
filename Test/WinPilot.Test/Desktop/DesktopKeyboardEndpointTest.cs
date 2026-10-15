using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Core.Input;
using WinPilot.Features.Desktop;
using WinPilot.Infrastructure;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class DesktopKeyboardEndpointTest
{
    [Fact]
    public async Task RequestCancellationPropagatesWithoutSendingKeys()
    {
        var simulator = new RecordingSimulator(null);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new KeyboardInputService(simulator));
        await using var app = builder.Build();
        var propagated = false;
        app.Use(async (context, next) =>
        {
            context.RequestAborted = new CancellationToken(true);
            try { await next(context); }
            catch (OperationCanceledException) { propagated = true; }
        });
        app.MapDesktopKeyboardEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/desktop/keyboard", new { keys = new[] { "F11" } });
        Assert.True(propagated);
        Assert.Null(simulator.Keys);
        Assert.NotEqual(499, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("valid", 0, HttpStatusCode.NoContent)]
    [InlineData("unsupported", 0, HttpStatusCode.BadRequest)]
    [InlineData("duplicate", 0, HttpStatusCode.BadRequest)]
    [InlineData("empty", 0, HttpStatusCode.BadRequest)]
    [InlineData("missing", 0, HttpStatusCode.BadRequest)]
    [InlineData("system", 0, HttpStatusCode.InternalServerError)]
    [InlineData("system", 5, HttpStatusCode.InternalServerError)]
    public async Task KeyboardEndpointDistinguishesValidationFromSystemFailure(string kind, int errorCode, HttpStatusCode expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Keyboard.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var cause = kind == "system" ? new Win32Exception(errorCode, "native-private-" + directory) : null;
            var simulator = new RecordingSimulator(cause);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            using var logs = new RollingFileLoggerProvider(directory);
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton(new KeyboardInputService(simulator));
            await using var app = builder.Build();
            app.MapDesktopKeyboardEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            string[]? keys = kind switch
            {
                "unsupported" => ["Unsupported"],
                "duplicate" => ["Ctrl", "Ctrl"],
                "empty" => [],
                "missing" => null,
                _ => ["F11"]
            };
            using var response = await client.PostAsJsonAsync("/api/desktop/keyboard", new { keys });
            Assert.Equal(expected, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (expected == HttpStatusCode.NoContent)
            {
                Assert.Empty(body);
                Assert.Equal(new[] { KeyboardKey.F11 }, simulator.Keys);
            }
            else
            {
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                using var problem = JsonDocument.Parse(body);
                Assert.Equal("键盘输入失败", problem.RootElement.GetProperty("title").GetString());
                Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());
                if (expected == HttpStatusCode.InternalServerError)
                {
                    Assert.Equal("无法发送键盘输入，请稍后重试", problem.RootElement.GetProperty("detail").GetString());
                    Assert.Equal(new[] { KeyboardKey.F11 }, simulator.Keys);
                    Assert.DoesNotContain(directory, body);
                    Assert.DoesNotContain("Win32Exception", body);
                    using var logStream = new FileStream(Directory.GetFiles(directory, "winpilot*.log").Single(),
                        FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var logReader = new StreamReader(logStream);
                    var log = logReader.ReadToEnd();
                    Assert.Contains("发送键盘输入失败", log);
                    Assert.Contains(cause!.Message, log);
                    Assert.Contains("Win32Exception", log);
                }
                else Assert.Null(simulator.Keys);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class RecordingSimulator(Exception? error) : IKeyboardInputSimulator
    {
        internal IReadOnlyList<KeyboardKey>? Keys { get; private set; }
        public void Send(IReadOnlyList<KeyboardKey> keys)
        {
            Keys = keys.ToArray();
            if (error is not null) throw error;
        }
    }
}
