using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Core;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class OneBotCommandBotTest
{
    [Fact]
    public void DefaultsAndMissingPrefixFieldsDoNotRequirePrefix()
    {
        var settings = JsonSerializer.Deserialize<CommandBotSettings>("{}")!;
        Assert.Equal("", settings.TelegramPrefix);
        Assert.Equal("", settings.FeishuPrefix);
        Assert.Equal("", settings.QQBotPrefix);
        Assert.Equal("", settings.OneBotPrefix);
        CommandBotService.ValidatePrefix("");
    }

    [Theory]
    [InlineData("/状态", "/", "状态")]
    [InlineData("  / 状态  ", "/", "状态")]
    [InlineData("状态 /", "/", null)]
    [InlineData("/", "/", null)]
    [InlineData(" /   ", "/", null)]
    [InlineData("状态", "/", null)]
    [InlineData("!!状态", "!!", "状态")]
    [InlineData("!状态", "!!", null)]
    [InlineData("状态", "", "状态")]
    [InlineData(" /状态 ", "", "/状态")]
    [InlineData("", "", null)]
    [InlineData("  ", "", null)]
    public void PrefixMatchesOnlyBeginningAndRequiresCommandText(string text, string prefix, string? command)
    {
        Assert.Equal(command, CommandBotService.ExtractCommand(text, prefix));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("/ ")]
    [InlineData("\n")]
    [InlineData("/\u0000")]
    public void InvalidPrefixesAreRejected(string prefix) =>
        Assert.Throws<InvalidOperationException>(() => CommandBotService.ValidatePrefix(prefix));

    [Fact]
    public async Task PrefixChangesWithoutReconnectAndMessagesWithoutPrefixAreIgnored()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        var saved = fixture.Bot.GetOptions();
        await fixture.Bot.SaveAsync(saved with { Prefix = "!!" }, CancellationToken.None);
        Assert.Equal(1, fixture.Attempts);
        var received = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Message("private", "no-prefix", "状态"));
        fixture.Receive!(Message("private", "old-prefix", "/状态"));
        fixture.Receive!(Message("private", "only-prefix", "!!"));
        Assert.False(received.IsCompleted);
        fixture.Receive!(Message("private", "new-prefix", "!! 帮助"));
        Assert.True((await received.WaitAsync(TimeSpan.FromSeconds(2))) > 0);
        Assert.Equal("!!", fixture.Bot.GetOptions().Prefix);
        await fixture.Bot.SaveAsync(saved with { Prefix = "" }, CancellationToken.None);
        Assert.Equal("", fixture.Bot.GetOptions().Prefix);
        Assert.Equal(1, fixture.Attempts);
        var unprefixed = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Message("private", "unprefixed", "帮助"));
        Assert.True((await unprefixed.WaitAsync(TimeSpan.FromSeconds(2))) > 0);
    }

    [Theory]
    [InlineData("[CQ:at,qq=bot] /状态", "状态")]
    [InlineData("[CQ:at,qq=other] /状态", null)]
    [InlineData("/[CQ:image,file=test]", null)]
    public void CqCommandsRemoveOnlyCurrentBotMentionAndIgnoreAttachments(string text, string? command)
    {
        var parsed = OneBotCommandBotTransport.ParseMessage(Message("group", "id", text))!.Value.Message;
        Assert.Equal(command, CommandBotService.ExtractCommand(parsed.Content, "/"));
    }

    [Fact]
    public void ArrayCommandsRemoveOnlyCurrentBotMention()
    {
        var message = OneBotCommandBotTransport.ParseMessage(Message("group", "id", new object[]
        {
            new { type = "at", data = new { qq = "bot" } }, new { type = "text", data = new { text = " / 状态" } }
        }))!.Value.Message;
        Assert.Equal("状态", CommandBotService.ExtractCommand(message.Content, "/"));
    }

    [Theory]
    [InlineData("", "", "private", "user", "user", false)]
    [InlineData("", "", "group", "group", "user", false)]
    [InlineData("user", "", "private", "user", "user", true)]
    [InlineData("user", "", "private", "other", "other", false)]
    [InlineData("user", "", "group", "group", "user", false)]
    [InlineData("", "group", "group", "group", "other", true)]
    [InlineData("", "group", "group", "other", "user", false)]
    [InlineData("", "group", "private", "user", "user", false)]
    [InlineData("user", "group", "private", "user", "user", true)]
    [InlineData("user", "group", "group", "group", "user", true)]
    [InlineData("user", "group", "group", "group", "other", false)]
    [InlineData("user", "group", "group", "other", "user", false)]
    [InlineData(" user ", " group ", "group", "group", "user", true)]
    public void ReceptionScopeUsesSavedUserAndGroup(string user, string group, string type, string chat, string sender, bool allowed)
    {
        var settings = new NotificationSettings { OneBotUserId = user, OneBotGroupId = group };
        var message = new CommandBotMessage(chat, sender, "测试");
        Assert.Equal(allowed, OneBotCommandBotReceiver.IsAllowed(settings, type, message));
    }

    [Theory]
    [InlineData("private", "user")]
    [InlineData("group", "group")]
    public void ParsesPrivateAndGroupIncludingTemporarySessions(string type, string chat)
    {
        var parsed = OneBotCommandBotTransport.ParseMessage(Message(type, "id", "hello"))!.Value;
        Assert.Equal(chat, parsed.Message.ChatId);
        Assert.Equal("user", parsed.Message.Sender);
        Assert.Equal("hello", parsed.Message.Content);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("group")]
    public void IgnoresMessagesSentBySelf(string type)
    {
        Assert.Null(OneBotCommandBotTransport.ParseMessage(Message(type, "id", "帮助", "user")));
    }

    [Fact]
    public void FormatsMixedSegmentsWithoutFetchingOrExposingAttachmentUrls()
    {
        var payload = Message("group", "id", new object[]
        {
            new { type = "text", data = new { text = "hello " } },
            new { type = "at", data = new { qq = "123" } },
            new { type = "image", data = new { url = "https://secret.test/image" } },
            new { type = "record", data = new { file = "secret-file" } },
            new { type = "video" }, new { type = "file" }, new { type = "face" }, new { type = "forward" }, new { type = "unknown" }
        });
        var text = OneBotCommandBotTransport.ParseMessage(payload)!.Value.Message.Content;
        Assert.Equal("hello @123", text);
        Assert.DoesNotContain("secret", text);
    }

    [Fact]
    public void FormatsCqStringAndEscapedTextAndBoundsDisplayLength()
    {
        var parsed = OneBotCommandBotTransport.ParseMessage(Message("private", "id", "&#91;字&#93;&amp;[CQ:at,qq=123][CQ:image,file=private-path][CQ:forward,id=secret]"))!.Value;
        Assert.Equal("[字]&@123", parsed.Message.Content);
        var literal = OneBotCommandBotTransport.ParseMessage(Message("private", "id", new[] { new { type = "text", data = new { text = "[CQ:image]" } } }))!.Value;
        Assert.Equal("[CQ:image]", literal.Message.Content);
        var longText = OneBotCommandBotTransport.ParseMessage(Message("private", "id", new string('a', 10000)))!.Value;
        Assert.Equal(8193, longText.Message.Content.Length);
        Assert.EndsWith("…", longText.Message.Content);
    }

    [Theory]
    [InlineData("http://127.0.0.1:6700/")]
    [InlineData("ws://127.0.0.1:6700/api/")]
    [InlineData("ws://user:secret@127.0.0.1:6700/")]
    [InlineData("ws://127.0.0.1:6700/?access_token=secret")]
    [InlineData("ws://127.0.0.1:6700/#secret")]
    [InlineData("")]
    public void RejectsInvalidEnabledConnectionWithoutEchoingUrlCredentials(string address)
    {
        var error = Assert.Throws<InvalidOperationException>(() => OneBotCommandBotService.Validate(new(true, address, "")));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void AcceptsFullCustomPathAndOptionalTokenButRejectsControlCharacters()
    {
        OneBotCommandBotService.Validate(new(true, "wss://example.test/custom/events?client=winpilot", ""));
        OneBotCommandBotService.Validate(new(false, "", ""));
        Assert.Throws<InvalidOperationException>(() => OneBotCommandBotService.Validate(new(true, "ws://127.0.0.1/", "token\r\nheader")));
    }

    [Fact]
    public async Task DisabledStartupDoesNotCreateReceiverAndSavingPreservesOtherChannelsAndNotifications()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.StartAsync(CancellationToken.None);
        Assert.Equal(fixture.Attempts, fixture.Cleanups);
        await using var watch = fixture.Bot.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("disabled", watch.Current.State);
        var before = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).Notifications;
        await fixture.Bot.SaveAsync(new(false, "ws://127.0.0.1:6700/", "independent"), CancellationToken.None);
        Assert.Equal(0, fixture.Attempts);
        Assert.Equal(fixture.Attempts, fixture.Cleanups);
        await fixture.Enable();
        var settings = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile);
        Assert.Equal(before, settings.Notifications);
        Assert.True(settings.CommandBots!.TelegramEnabled);
        Assert.True(settings.CommandBots.FeishuEnabled);
        Assert.True(settings.CommandBots.QQBotEnabled);
        Assert.True(settings.CommandBots.OneBotEnabled);
        Assert.Equal("ws://127.0.0.1:6700/", settings.CommandBots.OneBotWebSocketUrl);
        Assert.Equal("independent", fixture.Options!.Token);
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        Assert.Equal(fixture.Attempts, fixture.Cleanups);
        Assert.Equal(1, fixture.Cleanups);
    }

    [Fact]
    public async Task HttpChangesDoNotReconnectButOwnAddressAndTokenChangesDo()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, settings => settings with
        {
            OneBotEndpoint = "http://another.test/", OneBotToken = "another-http-token"
        }, CancellationToken.None);
        Assert.Equal(1, fixture.Attempts);
        var test = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Command("group", "id", "帮助"));
        Assert.True((await test) > 0);
        await fixture.Bot.SaveAsync(new(true, "ws://127.0.0.1:6701/event", "new-ws-token"), CancellationToken.None);
        await WaitUntil(() => fixture.Attempts == 2 && fixture.Bot.Status.State == "connected");
        Assert.Equal("new-ws-token", fixture.Options!.Token);
        var saved = fixture.Bot.GetOptions();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Bot.SaveAsync(new(true, "http://invalid/", ""), CancellationToken.None));
        Assert.Equal(saved, fixture.Bot.GetOptions());
    }

    [Fact]
    public async Task NotificationScopeChangesApplyWithoutChangingWebSocketOptions()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, settings => settings with
        {
            OneBotUserId = "another-user", OneBotGroupId = "another-group"
        }, CancellationToken.None);
        await WaitUntil(() => fixture.Attempts == 2 && fixture.Bot.Status.State == "connected");
        Assert.Equal("ws-only", fixture.Options!.Token);
        var received = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Command("group", "old-scope", "帮助"));
        Assert.False(received.IsCompleted);
        fixture.Receive!(JsonSerializer.SerializeToElement(new
        {
            post_type = "message", message_type = "group", self_id = "bot", message_id = "new-scope",
            user_id = "another-user", group_id = "another-group", message = "/帮助"
        }));
        Assert.True((await received.WaitAsync(TimeSpan.FromSeconds(2))) > 0);
    }

    [Fact]
    public async Task ConnectedSocketIgnoresAccountHealthAndUnknownEvents()
    {
        await using var fixture = new Fixture();
        fixture.EmitLifecycle = false;
        await fixture.Enable();
        fixture.Receive!(Heartbeat(false));
        fixture.Receive!(JsonSerializer.SerializeToElement(new
        {
            post_type = "meta_event", meta_event_type = "heartbeat", self_id = 1, status = new { good = false }
        }));
        fixture.Receive!(JsonSerializer.SerializeToElement(new
        {
            post_type = "message", message_type = "private", self_id = 1, message = "invalid"
        }));
        Assert.Equal("connected", fixture.Bot.Status.State);
        Assert.Null(fixture.Bot.Status.Error);
        var test = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Command("private", "valid", "帮助"));
        Assert.True((await test) > 0);
        fixture.Disconnect!.TrySetException(new IOException());
        await WaitUntil(() => fixture.Bot.Status.State == "reconnecting");
        await WaitUntil(() => fixture.Attempts == 2 && fixture.Bot.Status.State == "connected");
    }

    [Fact]
    public async Task ConnectingIsKeptUntilSocketConnectionCompletes()
    {
        await using var fixture = new Fixture();
        fixture.HoldConnection = true;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Attempts == 1);
        Assert.Equal("connecting", fixture.Bot.Status.State);
        fixture.ReleaseConnection.TrySetResult();
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
    }

    [Theory]
    [InlineData(8191, true, false)]
    [InlineData(8192, false, false)]
    [InlineData(8192, true, true)]
    [InlineData(8193, false, true)]
    public void SegmentTruncationShowsEllipsisOnlyWhenContentIsActuallyOmitted(int length, bool extra, bool truncated)
    {
        var segments = new List<object> { new { type = "text", data = new { text = new string('a', length) } } };
        segments.Add(new { type = "text", data = new { text = "" } });
        if (extra) segments.Add(new { type = "text", data = new { text = "b" } });
        var text = OneBotCommandBotTransport.ParseMessage(Message("group", "id", segments))!.Value.Message.Content;
        Assert.Equal(truncated, text.EndsWith('…'));
        Assert.Equal(truncated ? 8193 : length + (extra ? 1 : 0), text.Length);
    }

    [Fact]
    public async Task DisableIsNotBlockedByInvalidDraftAndEnableValidatesBeforeSaving()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        await fixture.Bot.SaveAsync(new(false, "unfinished-address", ""), CancellationToken.None);
        Assert.Equal("disabled", fixture.Bot.Status.State);
        Assert.Equal(fixture.Attempts, fixture.Cleanups);
        Assert.Equal("unfinished-address", fixture.Bot.GetOptions().WebSocketUrl);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Bot.SetEnabledAsync(true, CancellationToken.None));
        Assert.False(fixture.Bot.GetOptions().Enabled);
        Assert.Equal(1, fixture.Attempts);
    }

    [Fact]
    public async Task DeduplicatesAcrossReconnectButNotAcrossAccountsOrConversations()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        var first = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Command("group", "same", "帮助"));
        await first;
        fixture.Disconnect!.TrySetException(new IOException());
        await WaitUntil(() => fixture.Attempts == 2 && fixture.Bot.Status.State == "connected");
        var second = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Command("group", "same", "帮助"));
        Assert.False(second.IsCompleted);
        fixture.Receive!(Command("private", "same", "帮助"));
        Assert.True((await second) > 0);
        var third = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Command("group", "same", "帮助", "another-bot"));
        Assert.True((await third) > 0);
    }

    [Fact]
    public async Task AuthFailureStopsUntilReconnect()
    {
        await using var fixture = new Fixture();
        fixture.Failure = new OneBotConnectionException("OneBot 拒绝认证，请检查 WebSocket Token 后重新连接", true);
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "failed" && fixture.Cleanups == fixture.Attempts);
        Assert.Equal(1, fixture.Attempts);
        Assert.True(fixture.Bot.Status.Enabled);
        fixture.Failure = null;
        await fixture.Bot.ReconnectAsync(CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
        Assert.Equal("connected", fixture.Bot.Status.State);
    }

    [Fact]
    public async Task SafeModeBlocksConfigurationButKeepsExistingReception()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
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
        using var client = app.GetTestClient();
        const string route = "/api/settings/notifications/command-bots/onebot";
        var configuration = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        Assert.True(configuration.Headers.CacheControl!.NoStore);
        var options = await configuration.Content.ReadFromJsonAsync<OneBotCommandBotOptions>();
        Assert.Equal(fixture.Bot.GetOptions(), options);
        app.Services.GetRequiredService<SafeModeService>().SetEnabled(true);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(route + "/configuration", options)).StatusCode);
        Assert.Equal("connected", fixture.Bot.Status.State);
    }

    [Fact]
    public async Task TransportConnectsWithIndependentBearerHeaderAndHandlesRealServerEvents()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        var header = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Map("/", async context =>
        {
            header.TrySetResult(context.Request.Headers.Authorization.ToString());
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(Heartbeat(true)), WebSocketMessageType.Text, true, context.RequestAborted);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("http://", "ws://");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = false;
        await Assert.ThrowsAsync<IOException>(() => OneBotCommandBotTransport.RunAsync(new(true, address, "ws-only-token"), () => connected = true,
            payload => { if (OneBotCommandBotTransport.IsReceptionEvent(payload)) received.TrySetResult(); }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Bearer ws-only-token", await header.Task);
        Assert.True(received.Task.IsCompletedSuccessfully);
        Assert.True(connected);
    }

    [Fact]
    public async Task SessionReassemblesFragmentsAndCancellationAbortsWithoutBackgroundTasks()
    {
        using var socket = new TestSocket();
        using var cancellation = new CancellationTokenSource();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Heartbeat(true));
        socket.Incoming.Writer.TryWrite((bytes[..10], false));
        socket.Incoming.Writer.TryWrite((bytes[10..], true));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = OneBotCommandBotTransport.RunSessionAsync(socket, payload =>
        {
            Assert.True(OneBotCommandBotTransport.IsReceptionEvent(payload));
            received.TrySetResult();
        }, cancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task SessionRejectsOversizedFrameAndNonMessageEventsAreIgnored()
    {
        using var socket = new TestSocket();
        for (var count = 0; count <= 128; count++) socket.Incoming.Writer.TryWrite((new byte[8192], false));
        await Assert.ThrowsAsync<InvalidDataException>(() => OneBotCommandBotTransport.RunSessionAsync(socket, _ => { }, CancellationToken.None));
        Assert.True(socket.Aborted);
        var notice = JsonSerializer.SerializeToElement(new { post_type = "notice", self_id = 1, notice_type = "group_recall" });
        Assert.False(OneBotCommandBotTransport.IsReceptionEvent(notice));
        Assert.Null(OneBotCommandBotTransport.ParseMessage(notice));
    }

    private static JsonElement Command(string type, string id, string content, string self = "bot") => Message(type, id, "/" + content, self);

    private static JsonElement Message(string type, string id, object content, string self = "bot") => JsonSerializer.SerializeToElement(new
    {
        post_type = "message", message_type = type, sub_type = "group", self_id = self, message_id = id, user_id = "user", group_id = "group", message = content
    });
    private static JsonElement Lifecycle() => JsonSerializer.SerializeToElement(new { post_type = "meta_event", meta_event_type = "lifecycle", sub_type = "connect", self_id = 1 });
    private static JsonElement Heartbeat(bool online) => JsonSerializer.SerializeToElement(new
    {
        post_type = "meta_event", meta_event_type = "heartbeat", self_id = 1, interval = 15000, status = new { online, good = online }
    });
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public CommandBotConfiguration Configuration { get; } = new();
        public PortableDataPaths Paths { get; } = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "winpilot-onebot-tests", Guid.NewGuid().ToString("N")));
        public CommandBotTestObserver<OneBotCommandBotService> Logger { get; } = new();
        public OneBotCommandBotService Bot { get; }
        public volatile int Attempts;
        public volatile int Cleanups;
        public bool EmitLifecycle = true;
        public bool HoldConnection;
        public TaskCompletionSource ReleaseConnection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure;
        public OneBotCommandBotOptions? Options;
        public Action<JsonElement>? Receive;
        public TaskCompletionSource? Disconnect;
        public Fixture()
        {
            new WinPilotSettings { Port = 9123, Notifications = new()
            {
                OneBotEndpoint = "http://127.0.0.1:3000", OneBotToken = "http-only", OneBotUserId = "user", OneBotGroupId = "group"
            }, CommandBots = new()
            {
                TelegramEnabled = true, FeishuEnabled = true, QQBotEnabled = true, OneBotWebSocketUrl = "ws://127.0.0.1:6700/", OneBotToken = "ws-only", OneBotPrefix = "/"
            } }.Save(Paths.ConfigurationFile);
            Bot = new(Paths, Logger, Configuration, async (options, connected, receive, token) =>
            {
                Options = options;
                Receive = receive;
                Disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Increment(ref Attempts);
                try
                {
                    if (Failure is not null) throw Failure;
                    if (HoldConnection) await ReleaseConnection.Task.WaitAsync(token);
                    connected();
                    if (EmitLifecycle) receive(Lifecycle());
                    await Disconnect.Task.WaitAsync(token);
                }
                finally { Interlocked.Increment(ref Cleanups); }
            });
        }
        public async Task Enable()
        {
            await Bot.SetEnabledAsync(true, CancellationToken.None);
            await WaitUntil(() => Bot.Status.State == "connected");
        }
        public async ValueTask DisposeAsync()
        {
            await Bot.DisposeAsync();
            Configuration.Dispose();
            Directory.Delete(Paths.ApplicationDirectory, true);
        }
    }

    private sealed class TestSocket : WebSocket
    {
        public Channel<(byte[] Bytes, bool End)> Incoming { get; } = Channel.CreateUnbounded<(byte[], bool)>();
        public bool Aborted;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => Aborted = true;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            var part = await Incoming.Reader.ReadAsync(token);
            part.Bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new(part.Bytes.Length, WebSocketMessageType.Text, part.End);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token) => Task.CompletedTask;
    }
}
