using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Core;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;

namespace WinPilot.Test;

[Collection(TerminalTestCollection.Name)]
public sealed class TerminalEndpointTest : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinPilot.Terminal.Test", Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(PortableDataPaths.Create(root));
        builder.Services.AddSingleton<TerminalSessionManager>();
        builder.Services.AddSingleton<SafeModeService>();
        app = builder.Build();
        app.UseWebSockets();
        app.MapTerminalEndpoints();
        await app.StartAsync();
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"type\":\"unknown\"}")]
    [InlineData("{\"type\":\"input\"}")]
    [InlineData("{\"type\":\"resize\"}")]
    [InlineData("{\"type\":\"create\",\"shell\":\"unsupported\"}")]
    public async Task InvalidMessageReturnsErrorAndConnectionRemainsUsable(string payload)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/api/terminal/ws"), cancellation.Token);
        await Send(socket, payload, cancellation.Token);
        Assert.Equal("error", await ReceiveType(socket, cancellation.Token));
        await Send(socket, "{\"type\":\"unknown\"}", cancellation.Token);
        Assert.Equal("error", await ReceiveType(socket, cancellation.Token));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellation.Token);
    }

    [Fact]
    public async Task FragmentedMessagesAreAssembledAndConnectionRemainsUsable()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/api/terminal/ws"), cancellation.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"unk"), WebSocketMessageType.Text, false, cancellation.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("nown\"}"), WebSocketMessageType.Text, true, cancellation.Token);
        Assert.Equal("error", await ReceiveType(socket, cancellation.Token));
        await Send(socket, "{\"type\":\"unknown\"}", cancellation.Token);
        Assert.Equal("error", await ReceiveType(socket, cancellation.Token));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellation.Token);
    }

    [Fact]
    public async Task OversizedFragmentedMessageClosesConnection()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/api/terminal/ws"), cancellation.Token);
        await socket.SendAsync(new byte[12 * 1024], WebSocketMessageType.Text, false, cancellation.Token);
        await socket.SendAsync(new byte[12 * 1024], WebSocketMessageType.Text, true, cancellation.Token);
        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, cancellation.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
    }

    [Fact]
    public async Task BinaryMessageReturnsError()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/api/terminal/ws"), cancellation.Token);
        await socket.SendAsync(new byte[] { 1, 2 }, WebSocketMessageType.Binary, true, cancellation.Token);
        Assert.Equal("error", await ReceiveType(socket, cancellation.Token));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellation.Token);
    }

    [Fact]
    public async Task DisconnectClosesCreatedSession()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/api/terminal/ws"), cancellation.Token);
        var manager = app.Services.GetRequiredService<TerminalSessionManager>();
        await Send(socket, JsonSerializer.Serialize(new { type = "create", shell = "cmd", workingDirectory = root }), cancellation.Token);
        while (await ReceiveType(socket, cancellation.Token) != "created") { }
        Assert.Equal(1, manager.ActiveSessionCount);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellation.Token);
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    private static Task Send(WebSocket socket, string payload, CancellationToken token)
        => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)), WebSocketMessageType.Text, true, token);

    private static async Task<string?> ReceiveType(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        Assert.True(result.EndOfMessage);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return document.RootElement.GetProperty("type").GetString();
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
