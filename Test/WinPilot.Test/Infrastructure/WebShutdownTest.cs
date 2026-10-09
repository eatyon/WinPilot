using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinPilot.Core;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class WebShutdownTest
{
    [Theory]
    [InlineData("sse")]
    [InlineData("terminal")]
    [InlineData("request")]
    public async Task ShutdownEndsOpenConnectionWithoutWaitingForClient(string connection)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Shutdown.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
            builder.Services.AddSingleton(PortableDataPaths.Create(root));
            builder.Services.AddSingleton<TerminalSessionManager>();
            builder.Services.AddSingleton<SafeModeService>();
            await using var app = builder.Build();
            app.UseMiddleware<RequestCancellationMiddleware>();
            app.UseWebSockets();
            app.MapSafeModeEndpoints();
            app.MapTerminalEndpoints();
            app.MapGet("/waiting", async (HttpContext context) =>
            {
                await context.Response.WriteAsync("waiting", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            });
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var client = new HttpClient();
            using var socket = new ClientWebSocket();
            HttpResponseMessage? response = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (connection == "terminal")
                {
                    await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/api/terminal/ws" }.Uri, timeout.Token);
                    Assert.Equal(WebSocketState.Open, socket.State);
                }
                else
                {
                    response = await client.GetAsync(new Uri(address, connection == "sse" ? "/api/security/safe-mode/events" : "/waiting"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                }
                await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(app.Lifetime.ApplicationStopped.IsCancellationRequested);
            }
            finally
            {
                response?.Dispose();
                socket.Abort();
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
