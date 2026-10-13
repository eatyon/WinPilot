using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Core;
using WinPilot.Features.Notifications;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class QQBotCommandBotTest
{
    [Theory]
    [InlineData("<@!bot> /状态", "状态")]
    [InlineData("<@bot>/状态", "状态")]
    [InlineData("<@other> /状态", null)]
    public void CommandsStripOnlyCurrentBotMention(string text, string? expected)
    {
        var message = QQBotCommandBotTransport.ParseMessage(Message("GROUP_MESSAGE_CREATE", "id", text), "bot")!;
        Assert.Equal(expected, CommandBotService.ExtractCommand(message.CommandText!, "/"));
    }

    [Fact]
    public async Task PrefixChangesWithoutReconnectingAndBindingRemainsPrefixIndependent()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        await fixture.Bot.SaveSettingsAsync(new(true, "!!"), CancellationToken.None);
        Assert.Equal(1, fixture.Attempts);
        Assert.Equal("!!", fixture.Bot.GetSettings().Prefix);
        var received = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Message("C2C_MESSAGE_CREATE", "ignored", "/状态"));
        Assert.False(received.IsCompleted);
        fixture.Receive!(Message("C2C_MESSAGE_CREATE", "allowed", "!!帮助"));
        Assert.True((await received.WaitAsync(TimeSpan.FromSeconds(2))) > 0);
        var group = await fixture.Bot.BindAsync(fixture.Settings!, true, (code, _) =>
        {
            fixture.Receive!(Message("GROUP_MESSAGE_CREATE", "binding-no-prefix", code));
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("group", group);
    }

    [Theory]
    [InlineData("", "", "C2C_MESSAGE_CREATE", "user", "user", false)]
    [InlineData("", "", "GROUP_MESSAGE_CREATE", "group", "user", false)]
    [InlineData("user", "", "C2C_MESSAGE_CREATE", "user", "user", true)]
    [InlineData("user", "", "C2C_MESSAGE_CREATE", "other", "other", false)]
    [InlineData("user", "", "GROUP_MESSAGE_CREATE", "group", "user", false)]
    [InlineData("", "group", "GROUP_MESSAGE_CREATE", "group", "other", true)]
    [InlineData("", "group", "GROUP_MESSAGE_CREATE", "other", "user", false)]
    [InlineData("", "group", "C2C_MESSAGE_CREATE", "user", "user", false)]
    [InlineData("user", "group", "C2C_MESSAGE_CREATE", "user", "user", true)]
    [InlineData("user", "group", "GROUP_MESSAGE_CREATE", "group", "user", true)]
    [InlineData("user", "group", "GROUP_MESSAGE_CREATE", "group", "other", false)]
    [InlineData("user", "group", "GROUP_MESSAGE_CREATE", "other", "user", false)]
    [InlineData(" user ", " group ", "GROUP_MESSAGE_CREATE", "group", "user", true)]
    public void ReceptionScopeUsesSavedUserAndGroup(string user, string group, string type, string chat, string sender, bool allowed)
    {
        var settings = new NotificationSettings { QQBotOpenId = user, QQBotGroupOpenId = group };
        var message = new CommandBotMessage(chat, sender, "测试");
        Assert.Equal(allowed, QQBotCommandBotReceiver.IsAllowed(settings, type, message));
    }

    [Theory]
    [InlineData("C2C_MESSAGE_CREATE", "user")]
    [InlineData("GROUP_AT_MESSAGE_CREATE", "group")]
    [InlineData("GROUP_MESSAGE_CREATE", "group")]
    public void ParsesAllReceptionModes(string type, string chat)
    {
        var result = QQBotCommandBotTransport.ParseMessage(Message(type, "id"));
        Assert.NotNull(result);
        Assert.Equal(chat, result.ChatId);
        Assert.Equal("user", result.Sender);
        Assert.Equal("测试", result.Content);
    }

    [Theory]
    [InlineData("C2C_MESSAGE_CREATE", true)]
    [InlineData("GROUP_AT_MESSAGE_CREATE", true)]
    [InlineData("GROUP_MESSAGE_CREATE", true)]
    [InlineData("C2C_MESSAGE_CREATE", false)]
    [InlineData("GROUP_AT_MESSAGE_CREATE", false)]
    [InlineData("GROUP_MESSAGE_CREATE", false)]
    public void FiltersBotSendersInAllReceptionModes(string type, bool isBot)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            op = 0, t = type, d = new
            {
                id = "id", content = "帮助", group_openid = "group",
                author = new { user_openid = "user", member_openid = "user", bot = isBot }
            }
        });
        var result = QQBotCommandBotTransport.ParseMessage(payload);
        if (isBot) Assert.Null(result);
        else Assert.Equal("帮助", result!.Content);
    }

    [Fact]
    public void ParsesGroupMemberOpenIdAndNonTextWithoutInventingIdentity()
    {
        using var document = JsonDocument.Parse("""{"op":0,"t":"GROUP_MESSAGE_CREATE","d":{"group_openid":"g","author":{"member_openid":"m"},"attachments":[{}]}}""");
        var result = QQBotCommandBotTransport.ParseMessage(document.RootElement)!;
        Assert.Equal("m", result.Sender);
        Assert.Equal("[非文字消息]", result.Content);
        using var invalid = JsonDocument.Parse("""{"op":0,"t":"GROUP_MESSAGE_CREATE","d":{"author":{}}}""");
        Assert.Null(QQBotCommandBotTransport.ParseMessage(invalid.RootElement));
        Assert.Null(QQBotCommandBotTransport.ParseMessage(Message("FRIEND_ADD", "id")));
    }

    [Fact]
    public async Task DisabledDoesNotCreateReceiverAndEnabledNeedsOnlySharedCredentials()
    {
        await using var fixture = new Fixture();
        await fixture.Bot.StartAsync(CancellationToken.None);
        Assert.Equal(fixture.Attempts, fixture.Cleanups);
        Assert.Equal(0, fixture.Attempts);
        await using var watch = fixture.Bot.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("disabled", watch.Current.State);
        var before = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).Notifications;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
            await WaitUntil(() => fixture.Bot.Status.State == "connected");
            Assert.Equal(attempt, fixture.Attempts);
            Assert.True(fixture.Attempts > fixture.Cleanups);
            var settings = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile);
            Assert.Equal(before, settings.Notifications);
            Assert.True(settings.CommandBots!.TelegramEnabled);
            Assert.True(settings.CommandBots.FeishuEnabled);
            Assert.True(settings.CommandBots.QQBotEnabled);
            await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
            Assert.Equal(fixture.Attempts, fixture.Cleanups);
            Assert.Equal(attempt, fixture.Cleanups);
        }
    }

    [Fact]
    public async Task SavedMessageFormatRefreshesEnabledBotAndPreservesOtherSettings()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        var previous = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile);
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile,
            settings => settings with { QQBotMessageFormat = "markdown" }, CancellationToken.None);
        await WaitUntil(() => fixture.Attempts == 2 && fixture.Bot.Status.State == "connected");
        Assert.Equal("markdown", fixture.Settings!.QQBotMessageFormat);
        var current = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile);
        Assert.Equal(previous.Notifications! with { QQBotMessageFormat = "markdown" }, current.Notifications);
        Assert.Equal(previous.CommandBots, current.CommandBots);
    }

    [Fact]
    public async Task NotificationScopeChangesReconnectAndApplyNewRange()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, s => s with { QQBotOpenId = "another-user", QQBotGroupOpenId = "another-group" }, CancellationToken.None);
        await WaitUntil(() => fixture.Attempts == 2 && fixture.Bot.Status.State == "connected");
        var test = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Message("GROUP_MESSAGE_CREATE", "id", "/帮助"));
        Assert.False(test.IsCompleted);
        fixture.Receive!(JsonSerializer.SerializeToElement(new
        {
            op = 0, t = "GROUP_MESSAGE_CREATE", d = new
            {
                id = "new-range", content = "/帮助", group_openid = "another-group", author = new { member_openid = "another-user" }
            }
        }));
        Assert.True((await test.WaitAsync(TimeSpan.FromSeconds(2))) > 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, s => s with { QQBotClientSecret = "" }, CancellationToken.None));
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, s => s with { QQBotClientSecret = "new-secret" }, CancellationToken.None);
        await WaitUntil(() => fixture.Attempts == 3 && fixture.Bot.Status.State == "connected");
        Assert.Equal("new-secret", fixture.Settings!.QQBotClientSecret);
    }

    [Fact]
    public async Task BindingReusesReceiverAndCancellationAndShutdownReleaseWaiters()
    {
        await using var fixture = new Fixture();
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, settings => settings with { QQBotOpenId = "", QQBotGroupOpenId = "" }, CancellationToken.None);
        await fixture.Enable();
        var result = await fixture.Bot.BindAsync(fixture.Settings!, true, (code, _) =>
        {
            fixture.Receive!(Message("GROUP_MESSAGE_CREATE", "binding", code));
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("group", result);
        Assert.Equal(0, fixture.Logger.Count);
        Assert.Equal(1, fixture.Attempts);
        using var cancel = new CancellationTokenSource();
        var cancelled = fixture.Bot.BindAsync(fixture.Settings!, false, (_, _) => Task.CompletedTask, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal("connected", fixture.Bot.Status.State);
        var pending = fixture.Bot.BindAsync(fixture.Settings!, false, (_, _) => Task.CompletedTask, CancellationToken.None);
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DeduplicatesAcrossReconnectAndAcrossGroupEventTypes()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        var first = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Message("GROUP_AT_MESSAGE_CREATE", "same", "/帮助"));
        await first;
        fixture.Disconnect!.TrySetException(new IOException());
        await WaitUntil(() => fixture.Attempts == 2 && fixture.Bot.Status.State == "connected");
        var second = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        fixture.Receive!(Message("GROUP_MESSAGE_CREATE", "same", "/帮助"));
        Assert.False(second.IsCompleted);
        fixture.Receive!(Message("C2C_MESSAGE_CREATE", "new", "/帮助"));
        Assert.True((await second) > 0);
    }

    [Fact]
    public async Task ConnectedBindingRejectsConcurrentRequestsAndReleasesSlotAfterSuccessAndCancellation()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        var code = "";
        var first = fixture.Bot.BindAsync(fixture.Settings!, true, (value, _) =>
        {
            code = value;
            return Task.CompletedTask;
        }, CancellationToken.None);
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Bot.BindAsync(
            fixture.Settings!, false, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("正在获取", busy.Message);
        Assert.False(first.IsCompleted);
        fixture.Receive!(Message("GROUP_MESSAGE_CREATE", "first-binding", code));
        Assert.Equal("group", await first);
        using var cancellation = new CancellationTokenSource();
        var cancelled = fixture.Bot.BindAsync(fixture.Settings!, true, (_, _) => Task.CompletedTask, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal("user", await fixture.Bot.BindAsync(fixture.Settings!, false, (value, _) =>
        {
            fixture.Receive!(Message("C2C_MESSAGE_CREATE", "next-binding", value));
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.Equal(1, fixture.Attempts);
    }

    [Fact]
    public async Task TemporaryBindingRejectsConcurrentRequestsAndCancellationReleasesConnection()
    {
        await using var fixture = new Fixture();
        var settings = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).Notifications!;
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Bot.BindAsync(settings, false, (_, _) => Task.CompletedTask, cancellation.Token);
        await fixture.TemporaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Bot.BindAsync(settings, true,
            (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(1, fixture.TemporaryAttempts);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, fixture.TemporaryCleanups);
        var next = fixture.Bot.BindAsync(settings, true, (_, _) => Task.CompletedTask, CancellationToken.None);
        fixture.TemporaryResult.TrySetResult("group");
        Assert.Equal("group", await next);
        Assert.Equal(2, fixture.TemporaryAttempts);
        Assert.Equal(2, fixture.TemporaryCleanups);
        Assert.Equal(0, fixture.Attempts);
        Assert.Equal(fixture.Attempts, fixture.Cleanups);
    }

    [Fact]
    public async Task EnablingRobotCancelsTemporaryBindingAndWaitsForSocketCleanupBeforeConnecting()
    {
        await using var fixture = new Fixture();
        fixture.HoldTemporaryCleanup = true;
        var settings = WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).Notifications!;
        var pending = fixture.Bot.BindAsync(settings, false, (_, _) => Task.CompletedTask, CancellationToken.None);
        await fixture.TemporaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.TemporaryStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, fixture.Attempts);
        Assert.Equal("connecting", fixture.Bot.Status.State);
        fixture.ReleaseTemporaryCleanup.TrySetResult();
        var cancelled = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("本次获取已取消", cancelled.Message);
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
        Assert.Equal(1, fixture.TemporaryCleanups);
        Assert.Equal(1, fixture.Attempts);
        Assert.Equal("user", await fixture.Bot.BindAsync(settings, false, (code, _) =>
        {
            fixture.Receive!(Message("C2C_MESSAGE_CREATE", "reused", code));
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.Equal(1, fixture.TemporaryAttempts);
    }

    [Fact]
    public async Task InvalidCredentialsStayEnabledButReleaseReceiverUntilManualReconnect()
    {
        await using var fixture = new Fixture();
        fixture.Failure = new HttpRequestException("secret must not appear", null, HttpStatusCode.Unauthorized);
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "failed" && fixture.Cleanups == fixture.Attempts);
        Assert.True(fixture.Bot.Status.Enabled);
        Assert.DoesNotContain("secret must not appear", fixture.Bot.Status.Error!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Bot.BindAsync(new(), false, (_, _) => Task.CompletedTask, CancellationToken.None));
        fixture.Failure = null;
        await fixture.Bot.ReconnectAsync(CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "connected");
    }

    [Fact]
    public async Task SessionIdentifiesAndKeepsSequenceForResume()
    {
        using var socket = new TestSocket { AutoAck = true };
        using var cancellation = new CancellationTokenSource();
        var resume = new QQBotGatewaySession();
        socket.Enqueue(new { op = 10, d = new { heartbeat_interval = 20 } });
        socket.Enqueue(new { op = 0, s = 1, t = "READY", d = new { session_id = "session" } });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = QQBotCommandBotTransport.RunSessionAsync(socket, "access", resume, () => ready.TrySetResult(), _ => { }, cancellation.Token);
        await ready.Task;
        var identify = await socket.Sent.Reader.ReadAsync();
        Assert.Equal(2, identify.GetProperty("op").GetInt32());
        Assert.Equal(1 << 25, identify.GetProperty("d").GetProperty("intents").GetInt32());
        var heartbeat = await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, heartbeat.GetProperty("d").GetInt64());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(socket.Aborted);
        Assert.Equal("session", resume.Id);
        using var resumed = new TestSocket();
        resumed.Enqueue(new { op = 10, d = new { heartbeat_interval = 10000 } });
        resumed.Enqueue(new { op = 0, s = 2, t = "RESUMED", d = new { } });
        using var next = new CancellationTokenSource();
        var nextRun = QQBotCommandBotTransport.RunSessionAsync(resumed, "fresh-access", resume, () => next.Cancel(), _ => { }, next.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextRun);
        var request = await resumed.Sent.Reader.ReadAsync();
        Assert.Equal(6, request.GetProperty("op").GetInt32());
        Assert.Equal("QQBot fresh-access", request.GetProperty("d").GetProperty("token").GetString());
    }

    [Fact]
    public async Task ExpiredTokenRefreshesOnceAndRepeatedAuthenticationFailureStops()
    {
        await using var fixture = new Fixture();
        fixture.Failure = new QQBotGatewayClosedException(4004);
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await WaitUntil(() => fixture.Bot.Status.State == "failed" && fixture.Cleanups == fixture.Attempts);
        Assert.Equal(2, fixture.Attempts);
        Assert.True(fixture.Bot.Status.Enabled);
    }

    [Fact]
    public async Task MissingCredentialsRejectEnableWithoutCreatingReceiver()
    {
        await using var fixture = new Fixture();
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, settings => settings with { QQBotAppId = "" }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Bot.SetEnabledAsync(true, CancellationToken.None));
        Assert.Equal(fixture.Attempts, fixture.Cleanups);
        Assert.False(WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).CommandBots!.QQBotEnabled);
    }

    [Fact]
    public async Task SafeModeBlocksSettingsAndTestButKeepsReceptionRunning()
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
        const string route = "/api/settings/notifications/command-bots/qqbot";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/notifications/command-bots/qqbot/status")).StatusCode);
        app.Services.GetRequiredService<SafeModeService>().SetEnabled(true);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(route, new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(route + "/reconnect", null)).StatusCode);
        Assert.Equal("connected", fixture.Bot.Status.State);
    }

    [Fact]
    public async Task MissingHeartbeatAckAbortsReceiveAndInvalidSessionResetsResume()
    {
        using var socket = new TestSocket();
        socket.Enqueue(new { op = 10, d = new { heartbeat_interval = 5 } });
        socket.Enqueue(new { op = 0, s = 1, t = "READY", d = new { session_id = "s" } });
        await Assert.ThrowsAnyAsync<Exception>(() => QQBotCommandBotTransport.RunSessionAsync(socket, "access", new(), () => { }, _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(socket.Aborted);
        using var invalid = new TestSocket();
        invalid.Enqueue(new { op = 10, d = new { heartbeat_interval = 10000 } });
        invalid.Enqueue(new { op = 9, d = false });
        var resume = new QQBotGatewaySession { Id = "old", Sequence = 1 };
        await Assert.ThrowsAsync<IOException>(() => QQBotCommandBotTransport.RunSessionAsync(invalid, "access", resume, () => { }, _ => { }, CancellationToken.None));
        Assert.Null(resume.Id);
        Assert.Null(resume.Sequence);
    }

    private static JsonElement Message(string type, string id, string content = "测试") => JsonSerializer.SerializeToElement(new
    {
        op = 0, t = type, d = new { id, content, group_openid = "group", author = type == "C2C_MESSAGE_CREATE" ? (object)new { user_openid = "user" } : new { member_openid = "user" } }
    });

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public CommandBotConfiguration Configuration { get; } = new();
        public PortableDataPaths Paths { get; } = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "winpilot-qqbot-tests", Guid.NewGuid().ToString("N")));
        public CommandBotTestObserver<QQBotCommandBotService> Logger { get; } = new();
        public QQBotCommandBotService Bot { get; }
        public volatile int Attempts;
        public volatile int Cleanups;
        public NotificationSettings? Settings;
        public Action<JsonElement>? Receive;
        public Exception? Failure;
        public TaskCompletionSource? Disconnect;
        public volatile int TemporaryAttempts;
        public volatile int TemporaryCleanups;
        public bool HoldTemporaryCleanup;
        public TaskCompletionSource TemporaryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TemporaryStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTemporaryCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> TemporaryResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Fixture()
        {
            new WinPilotSettings { Port = 9123, Notifications = new() { QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "user", QQBotGroupOpenId = "group" },
                CommandBots = new() { TelegramEnabled = true, FeishuEnabled = true, QQBotPrefix = "/" } }.Save(Paths.ConfigurationFile);
            Bot = new(Paths, Logger, Configuration, async (settings, session, connected, receive, token) =>
            {
                Settings = settings;
                Receive = receive;
                Disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Increment(ref Attempts);
                try
                {
                    if (Failure is not null) throw Failure;
                    connected();
                    await Disconnect.Task.WaitAsync(token);
                }
                finally { Interlocked.Increment(ref Cleanups); }
            }, async (settings, group, reportCode, token) =>
            {
                Interlocked.Increment(ref TemporaryAttempts);
                try
                {
                    await reportCode("1234", token);
                    TemporaryStarted.TrySetResult();
                    return await TemporaryResult.Task.WaitAsync(token);
                }
                finally
                {
                    TemporaryStopped.TrySetResult();
                    if (HoldTemporaryCleanup) await ReleaseTemporaryCleanup.Task;
                    Interlocked.Increment(ref TemporaryCleanups);
                }
            });
        }
        public async Task Enable()
        {
            await Bot.SetEnabledAsync(true, CancellationToken.None);
            await WaitUntil(() => Bot.Status.State == "connected");
        }
        public async ValueTask DisposeAsync()
        {
            ReleaseTemporaryCleanup.TrySetResult();
            await Bot.DisposeAsync();
            Configuration.Dispose();
            Directory.Delete(Paths.ApplicationDirectory, true);
        }
    }

    private sealed class TestSocket : WebSocket
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        public Channel<JsonElement> Sent { get; } = Channel.CreateUnbounded<JsonElement>();
        public bool AutoAck;
        public bool Aborted;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public void Enqueue(object value) => incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
        public override void Abort() => Aborted = true;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            var bytes = await incoming.Reader.ReadAsync(token);
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.AsSpan()));
            var root = document.RootElement.Clone();
            Sent.Writer.TryWrite(root);
            if (AutoAck && root.GetProperty("op").GetInt32() == 1) Enqueue(new { op = 11 });
            return Task.CompletedTask;
        }
    }
}
