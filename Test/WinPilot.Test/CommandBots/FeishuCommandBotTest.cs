using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProtoBuf;
using WinPilot.Core;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class FeishuCommandBotTest
{
    [Theory]
    [InlineData("bot", "状态")]
    [InlineData("other", null)]
    public void PrefixCommandsRemoveOnlyMentionOfCurrentBot(string mentionedId, string? expected)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            header = new { event_type = "im.message.receive_v1" },
            @event = new
            {
                sender = new { sender_type = "user", sender_id = new { open_id = "user" } },
                message = new
                {
                    chat_id = "chat", message_type = "text", content = JsonSerializer.Serialize(new { text = "@_user_1 /状态" }),
                    mentions = new[] { new { key = "@_user_1", name = "机器人", id = new { open_id = mentionedId } } }
                }
            }
        });
        var message = FeishuCommandBotTransport.ParseMessage(payload, "bot")!;
        Assert.Equal(expected, CommandBotService.ExtractCommand(message.CommandText!, "/"));
    }

    [Fact]
    public async Task DisabledStartupAndStatusWatchDoNotCreateReceiverAndToggleReleasesIt()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.StartAsync(CancellationToken.None);
        Assert.Equal(fixture.Attempts, fixture.Cleanups);
        Assert.Equal(0, fixture.Attempts);
        await using var watcher = fixture.Bot.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        Assert.Equal("disabled", watcher.Current.State);
        Assert.Equal(fixture.Attempts, fixture.Cleanups);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
            await WaitUntil(() => fixture.Bot.Status.State == "connected");
            Assert.True(fixture.Attempts > fixture.Cleanups);
            Assert.Equal(attempt, fixture.Attempts);
            await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
            Assert.Equal(fixture.Attempts, fixture.Cleanups);
        }
    }

    [Fact]
    public async Task SharesCredentialsWithoutWebhookAndPersistsOnlyOwnSwitch()
    {
        await using var fixture = new Fixture();
        var before = JsonDocument.Parse(File.ReadAllText(fixture.Paths.ConfigurationFile));
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
        using var after = JsonDocument.Parse(File.ReadAllText(fixture.Paths.ConfigurationFile));
        Assert.Equal(before.RootElement.GetProperty("notifications").GetRawText(), after.RootElement.GetProperty("notifications").GetRawText());
        var saved = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile);
        Assert.True(saved.CommandBots!.FeishuEnabled);
        Assert.True(saved.CommandBots.TelegramEnabled);
        Assert.False(saved.Notifications!.FeishuEnabled);
        fixture.Receive!(new("chat", "user", "/测试"));
        Assert.Equal(0, fixture.Logger.Count);
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        Assert.Equal("disabled", fixture.Bot.Status.State);
        Assert.True(WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).CommandBots!.TelegramEnabled);
        before.Dispose();
    }

    [Fact]
    public async Task ChangingCredentialsRestartsButWebhookDoesNotAndInvalidFieldsDoNotSave()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Attempts == 1);
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, settings => settings with { FeishuWebhookUrl = "https://example.test/hook" }, CancellationToken.None);
        Assert.Equal(1, fixture.Attempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile,
            settings => settings with { FeishuAppSecret = "" }, CancellationToken.None));
        Assert.Equal("test-secret", WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).Notifications!.FeishuAppSecret);
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, settings => settings with { FeishuAppSecret = "new-secret" }, CancellationToken.None);
        await WaitUntil(() => fixture.Attempts == 2);
        Assert.Equal("new-secret", fixture.ConnectionSettings!.FeishuAppSecret);
    }

    [Fact]
    public async Task InvalidCredentialsRemainEnabledAndStopRetryingUntilReconnect()
    {
        await using var fixture = new Fixture();
        fixture.PermanentError = true;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "failed");
        await WaitUntil(() => fixture.Cleanups == fixture.Attempts);
        Assert.True(fixture.Bot.Status.Enabled);
        Assert.Equal(1, fixture.Attempts);
        fixture.PermanentError = false;
        await fixture.Bot.ReconnectAsync(CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
        Assert.Equal(2, fixture.Attempts);
    }

    [Fact]
    public async Task NetworkFaultRetriesAndRestoresReception()
    {
        await using var fixture = new Fixture();
        fixture.TransientError = true;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "reconnecting");
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
        Assert.Equal("connected", fixture.Bot.Status.State);
    }

    [Fact]
    public async Task StartupRestoresEnabledAndMissingCredentialsFailWithoutNetwork()
    {
        await using var fixture = new Fixture();
        WinPilotSettings.Update(fixture.Paths.ConfigurationFile, settings => settings with
        { CommandBots = settings.CommandBots with { FeishuEnabled = true } });
        await fixture.Bot.StartAsync(CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
        await fixture.Bot.StopAsync(CancellationToken.None);
        WinPilotSettings.Update(fixture.Paths.ConfigurationFile, settings => settings with
        {
            Notifications = settings.Notifications with { FeishuAppId = "" },
            CommandBots = settings.CommandBots with { FeishuEnabled = true }
        });
        using var configuration = new CommandBotConfiguration();
        using var restored = new FeishuCommandBotService(fixture.Paths, NullLogger<FeishuCommandBotService>.Instance, configuration);
        await restored.StartAsync(CancellationToken.None);
        Assert.Equal("failed", restored.Status.State);
        Assert.Contains("AppId", restored.Status.Error!);
        await restored.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SafeModeBlocksFeishuMutationsButKeepsReceiverRunning()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Paths);
        builder.Services.AddSingleton(fixture.Bot);
        builder.Services.AddSingleton<TerminalSessionManager>();
        builder.Services.AddSingleton<SafeModeService>();
        await using var app = builder.Build();
        app.UseMiddleware<SafeModeMiddleware>();
        app.MapCommandBotEndpoints();
        await app.StartAsync();
        app.Services.GetRequiredService<SafeModeService>().SetEnabled(true);
        using var client = app.GetTestClient();
        const string route = "/api/settings/notifications/command-bots/feishu";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/notifications/command-bots/feishu/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(route, new { enabled = false })).StatusCode);
        using var safeConfiguration = await client.GetAsync(route);
        using var safeJson = JsonDocument.Parse(await safeConfiguration.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "enabled" }, safeJson.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(safeJson.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/notifications/command-bots/feishu/reconnect", null)).StatusCode);
        Assert.Equal("connected", fixture.Bot.Status.State);
    }

    [Fact]
    public void ParsesTextAndMentionsButIgnoresBotsAndNonTextEvents()
    {
        using var document = JsonDocument.Parse(Event("message-1"));
        var received = FeishuCommandBotTransport.ParseMessage(document.RootElement)!;
        Assert.Equal("chat-1", received.ChatId);
        Assert.Equal("user-1", received.Sender);
        Assert.Equal("@测试机器人 hello", received.Content);
        using var bot = JsonDocument.Parse(Event("message-2", "bot"));
        Assert.Null(FeishuCommandBotTransport.ParseMessage(bot.RootElement));
        using var app = JsonDocument.Parse(Event("message-4", "app"));
        Assert.Null(FeishuCommandBotTransport.ParseMessage(app.RootElement));
        using var image = JsonDocument.Parse(Event("message-3", messageType: "image"));
        Assert.Null(FeishuCommandBotTransport.ParseMessage(image.RootElement));
        using var other = JsonDocument.Parse("{\"header\":{\"event_type\":\"other\"}}");
        Assert.Null(FeishuCommandBotTransport.ParseMessage(other.RootElement));
    }

    [Fact]
    public void ReassemblesOutOfOrderFragmentsAndRejectsInvalidBounds()
    {
        var fragments = new Dictionary<string, FeishuCommandBotTransport.FragmentSet>();
        Assert.Null(FeishuCommandBotTransport.Combine(Fragment(1, "world"), fragments));
        Assert.Equal("hello world", Encoding.UTF8.GetString(FeishuCommandBotTransport.Combine(Fragment(0, "hello "), fragments)!));
        Assert.Empty(fragments);
        Assert.Throws<InvalidDataException>(() => FeishuCommandBotTransport.Combine(Fragment(2, "invalid"), fragments));
    }

    [Fact]
    public async Task ProtocolSendsPingAndAcknowledgesDuplicatesWithoutDeliveringTwice()
    {
        using var socket = new TestSocket();
        using var cancellation = new CancellationTokenSource();
        var messages = new List<CommandBotMessage>();
        var session = FeishuCommandBotTransport.RunSessionAsync(socket, 123, new(), messages.Add, new(), _ => { }, cancellation.Token);
        var ping = await socket.Sent.Reader.ReadAsync();
        Assert.Equal("ping", ping.Header("type"));
        Assert.Equal(123, ping.Service);
        Assert.Equal(0, ping.Method);
        await socket.Incoming.Writer.WriteAsync(Encode(new() { Method = 1, Payload = Encoding.UTF8.GetBytes(Event("message-1")), Headers = [new() { Key = "type", Value = "event" }] }));
        var acknowledgment = await socket.Sent.Reader.ReadAsync();
        using var ack = JsonDocument.Parse(acknowledgment.Payload);
        Assert.Equal(200, ack.RootElement.GetProperty("code").GetInt32());
        await socket.Incoming.Writer.WriteAsync(Encode(new() { Method = 1, Payload = Encoding.UTF8.GetBytes(Event("message-1")), Headers = [new() { Key = "type", Value = "event" }] }));
        await socket.Sent.Reader.ReadAsync();
        Assert.Single(messages);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session);
    }

    [Theory]
    [InlineData(514, true)]
    [InlineData(403, true)]
    [InlineData(1000040350, true)]
    [InlineData(1, false)]
    [InlineData(1000040343, false)]
    public void EndpointErrorsDistinguishPermanentFailures(int code, bool permanent) =>
        Assert.Equal(permanent, FeishuCommandBotTransport.EndpointError(code, 200).Permanent);

    [Fact]
    public async Task BootstrapUsesExactCredentialFieldNamesAndReadsServerConfiguration()
    {
        using var handler = new BootstrapHandler();
        using var client = new HttpClient(handler);
        var (endpoint, service, config) = await FeishuCommandBotTransport.GetEndpointAsync(
            new() { FeishuAppId = " test-app ", FeishuAppSecret = " test-secret " }, client, CancellationToken.None);
        Assert.Equal("wss", endpoint.Scheme);
        Assert.Equal(123, service);
        Assert.Equal(60, config.PingInterval);
        Assert.Equal(3, config.ReconnectCount);
        Assert.Equal(120, config.ReconnectInterval);
        Assert.Equal(30, config.ReconnectNonce);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("test-app", request.RootElement.GetProperty("AppID").GetString());
        Assert.Equal("test-secret", request.RootElement.GetProperty("AppSecret").GetString());
        Assert.Equal(2, request.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void PingEncodingIncludesRequiredZeroFields()
    {
        var encoded = Encode(new() { Service = 123, Headers = [new() { Key = "type", Value = "ping" }] });
        Assert.Equal(new byte[] { 8, 0, 16, 0, 24, 123, 32, 0 }, encoded.Take(8));
    }

    [Fact]
    public async Task ConcurrentChannelSwitchesPreserveEachOther()
    {
        await using var fixture = new Fixture();
        using var telegram = new TelegramCommandBotService(fixture.Paths, NullLogger<TelegramCommandBotService>.Instance, fixture.Configuration);
        await Task.WhenAll(fixture.Bot.SetEnabledAsync(true, CancellationToken.None), telegram.SetEnabledAsync(false, CancellationToken.None));
        var settings = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile);
        Assert.True(settings.CommandBots!.FeishuEnabled);
        Assert.False(settings.CommandBots.TelegramEnabled);
        await telegram.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void RetryBackoffSurvivesShortConnectionsAndResetsOnlyAfterStableConnection()
    {
        var policy = new FeishuReconnectPolicy { Config = new(PingInterval: 10, ReconnectNonce: 0) };
        foreach (var expected in new[] { 2, 4, 8, 16, 30, 30 })
        {
            Assert.True(policy.TryNextDelay(TimeSpan.FromSeconds(1), 0, out var delay));
            Assert.Equal(TimeSpan.FromSeconds(expected), delay);
        }
        Assert.True(policy.TryNextDelay(TimeSpan.FromSeconds(29), 0, out var stillBackingOff));
        Assert.Equal(TimeSpan.FromSeconds(30), stillBackingOff);
        Assert.True(policy.TryNextDelay(TimeSpan.FromSeconds(30), 0, out var reset));
        Assert.Equal(TimeSpan.FromSeconds(2), reset);
    }

    [Fact]
    public void RetryUsesServerLimitsIntervalAndJitter()
    {
        var policy = new FeishuReconnectPolicy { Config = new(ReconnectCount: 2, ReconnectInterval: 120, ReconnectNonce: 30) };
        Assert.True(policy.TryNextDelay(null, 0.5, out var first));
        Assert.Equal(TimeSpan.FromSeconds(17), first);
        Assert.True(policy.TryNextDelay(TimeSpan.FromSeconds(1), 0.5, out var second));
        Assert.Equal(TimeSpan.FromSeconds(132), second);
        Assert.False(policy.TryNextDelay(null, 0, out _));
        Assert.True(policy.TryNextDelay(TimeSpan.FromSeconds(240), 0, out var restored));
        Assert.Equal(TimeSpan.FromSeconds(2), restored);
    }

    [Fact]
    public void PartialServerConfigurationPreservesPreviousValuesAndBoundsInvalidIntervals()
    {
        var previous = new FeishuClientConfig(60, 3, 120, 30);
        using var partial = JsonDocument.Parse("{\"PingInterval\":90,\"ReconnectCount\":0}");
        var next = FeishuCommandBotTransport.ReadConfig(partial.RootElement, previous);
        Assert.Equal(new FeishuClientConfig(90, 0, 120, 30), next);
        using var invalid = JsonDocument.Parse("{\"PingInterval\":0,\"ReconnectInterval\":-1,\"ReconnectNonce\":999999}");
        Assert.Equal(new FeishuClientConfig(60, 3, 120, 3600), FeishuCommandBotTransport.ReadConfig(invalid.RootElement, previous));
        Assert.False(new FeishuReconnectPolicy { Config = next }.TryNextDelay(null, 0, out _));
    }

    [Fact]
    public async Task PongUpdatesReconnectParametersForTheNextFailure()
    {
        using var socket = new TestSocket();
        using var cancellation = new CancellationTokenSource();
        FeishuClientConfig? updated = null;
        var session = FeishuCommandBotTransport.RunSessionAsync(socket, 123, new(), _ => { }, new(), config => updated = config, cancellation.Token);
        await socket.Sent.Reader.ReadAsync();
        await socket.Incoming.Writer.WriteAsync(Encode(new()
        {
            Headers = [new() { Key = "type", Value = "pong" }],
            Payload = JsonSerializer.SerializeToUtf8Bytes(new { PingInterval = 60, ReconnectCount = 2, ReconnectInterval = 120, ReconnectNonce = 30 })
        }));
        await socket.Incoming.Writer.WriteAsync(Encode(new()
        {
            Method = 1, Headers = [new() { Key = "type", Value = "event" }], Payload = Encoding.UTF8.GetBytes(Event("pong-test"))
        }));
        await socket.Sent.Reader.ReadAsync();
        Assert.Equal(new FeishuClientConfig(60, 2, 120, 30), updated);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session);
    }

    [Fact]
    public async Task ExhaustedServerRetriesStopAndManualReconnectStartsANewSequence()
    {
        await using var fixture = new Fixture();
        fixture.ServerConfig = new(ReconnectCount: 0);
        fixture.TransientError = true;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "failed");
        Assert.Equal(1, fixture.Attempts);
        Assert.True(fixture.Bot.Status.Enabled);
        Assert.Contains("上限", fixture.Bot.Status.Error!);
        await fixture.Bot.ReconnectAsync(CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
        Assert.Equal(2, fixture.Attempts);
    }

    [Fact]
    public async Task StopCancelsPendingRetryWithoutWaitingForItsDelay()
    {
        await using var fixture = new Fixture();
        fixture.ServerConfig = new(ReconnectNonce: 3600);
        fixture.TransientError = true;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "reconnecting");
        await fixture.Bot.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("disabled", fixture.Bot.Status.State);
        Assert.Equal(1, fixture.Attempts);
    }

    private static string Event(string id, string sender = "user", string messageType = "text") => JsonSerializer.Serialize(new
    {
        header = new { event_type = "im.message.receive_v1" },
        @event = new
        {
            sender = new { sender_type = sender, sender_id = new { open_id = "user-1" } },
            message = new { message_id = id, chat_id = "chat-1", message_type = messageType,
                content = JsonSerializer.Serialize(new { text = "@_user_1 hello" }), mentions = new[] { new { key = "@_user_1", name = "测试机器人" } } }
        }
    });
    private static FeishuFrame Fragment(int seq, string text) => new()
    {
        Payload = Encoding.UTF8.GetBytes(text), Headers = [new() { Key = "sum", Value = "2" }, new() { Key = "seq", Value = seq.ToString() }, new() { Key = "message_id", Value = "fragmented" }]
    };
    private static byte[] Encode(FeishuFrame frame)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, frame);
        return stream.ToArray();
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public CommandBotConfiguration Configuration { get; } = new();
        public PortableDataPaths Paths { get; } = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "winpilot-feishu-tests", Guid.NewGuid().ToString("N")));
        public CommandBotTestObserver<FeishuCommandBotService> Logger { get; } = new();
        public FeishuCommandBotService Bot { get; }
        public Action<CommandBotMessage>? Receive;
        public NotificationSettings? ConnectionSettings;
        public volatile int Attempts;
        public volatile int Cleanups;
        public bool PermanentError;
        public bool TransientError;
        public FeishuClientConfig? ServerConfig;
        public Fixture()
        {
            new WinPilotSettings { Port = 9123, Notifications = new() { FeishuAppId = "test-app", FeishuAppSecret = "test-secret" },
                CommandBots = new() { TelegramEnabled = true, FeishuPrefix = "/" } }.Save(Paths.ConfigurationFile);
            Bot = new(Paths, Logger, Configuration, async (settings, connected, receive, seen, configure, token) =>
            {
                ConnectionSettings = settings;
                Receive = receive;
                Interlocked.Increment(ref Attempts);
                try
                {
                    if (ServerConfig is not null) configure(ServerConfig);
                    if (PermanentError) throw FeishuCommandBotTransport.EndpointError(514, 200);
                    if (TransientError) { TransientError = false; throw new IOException(); }
                    connected();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally { Interlocked.Increment(ref Cleanups); }
            });
        }
        public async ValueTask DisposeAsync()
        {
            await Bot.StopAsync(CancellationToken.None);
            Bot.Dispose();
            Configuration.Dispose();
            Directory.Delete(Paths.ApplicationDirectory, true);
        }
    }

    private sealed class TestSocket : WebSocket
    {
        public Channel<byte[]> Incoming { get; } = Channel.CreateUnbounded<byte[]>();
        public Channel<FeishuFrame> Sent { get; } = Channel.CreateUnbounded<FeishuFrame>();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var bytes = await Incoming.Reader.ReadAsync(cancellationToken);
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Binary, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream(buffer.ToArray());
            Sent.Writer.TryWrite(Serializer.Deserialize<FeishuFrame>(stream));
            return Task.CompletedTask;
        }
    }

    private sealed class BootstrapHandler : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("https://open.feishu.cn/callback/ws/endpoint", request.RequestUri!.ToString());
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                code = 0, data = new { URL = "wss://example.test/socket?service_id=123", ClientConfig = new
                    { PingInterval = 60, ReconnectCount = 3, ReconnectInterval = 120, ReconnectNonce = 30 } }
            }, options: new JsonSerializerOptions()) };
        }
    }
}
