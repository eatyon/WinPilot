using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Notifications;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class TelegramCommandBotTest
{
    [Theory]
    [InlineData("@test_bot /状态", "状态")]
    [InlineData("@other /状态", null)]
    [InlineData("@test_bot_other /状态", null)]
    public void PrefixCommandsRemoveOnlyCurrentBotUsername(string text, string? expected)
    {
        var commandText = CommandBotService.StripLeadingBotMention(text, "@test_bot");
        Assert.Equal(expected, CommandBotService.ExtractCommand(commandText, "/"));
    }

    [Fact]
    public async Task DisabledStartupAndStatusWatchDoNotCreateReceiverAndToggleReleasesIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Bot.StartAsync(CancellationToken.None);
        await WaitUntil(() => fixture.ActivePolls == 0);
        Assert.Empty(fixture.Methods);
        await using var watcher = fixture.Bot.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        Assert.Equal("disabled", watcher.Current.State);
        await WaitUntil(() => fixture.ActivePolls == 0);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
            await fixture.WaitForState("connected");
            Assert.Contains("getMe", fixture.Methods);
            await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
            await WaitUntil(() => fixture.ActivePolls == 0);
            Assert.Equal("disabled", fixture.Bot.Status.State);
        }
    }

    [Fact]
    public async Task SafeModeBlocksBotChangesButKeepsReceptionRunning()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.WaitForState("connected");
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
        var safeMode = app.Services.GetRequiredService<SafeModeService>();
        using var removedRouteClient = app.GetTestClient();
        foreach (var channel in new[] { "telegram", "feishu", "qqbot", "onebot" })
            Assert.Equal(HttpStatusCode.NotFound, (await removedRouteClient.PostAsync($"/api/settings/command-bots/{channel}/test-receive", null)).StatusCode);
        safeMode.SetEnabled(true);
        using var client = app.GetTestClient();
        const string route = "/api/settings/command-bots/telegram";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(route, new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(route + "/reconnect", null)).StatusCode);
        var reception = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        await fixture.Updates.Writer.WriteAsync(new[] { Update(10, 123, "/帮助") });
        Assert.True((await reception.WaitAsync(TimeSpan.FromSeconds(5))) > 0);
        Assert.True(fixture.Bot.Status.Enabled);
    }

    [Fact]
    public async Task ReceiveFiltersChatSkipsBacklogAndDeduplicatesCommands()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.WaitForState("connected");
        var test = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        await fixture.Updates.Writer.WriteAsync(new[] { Update(10, 999, "忽略其他会话"), Update(11, 123, "/帮助") });
        var received = await test.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(received > 0);
        Assert.True(received > 0);
        Assert.False(WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).Notifications!.TelegramEnabled);
        Assert.True(fixture.Bot.Status.Enabled);
        var duplicate = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        await fixture.Updates.Writer.WriteAsync(new[] { Update(11, 123, "重复消息"), Update(12, 123, "/帮助") });
        Assert.True((await duplicate.WaitAsync(TimeSpan.FromSeconds(5))) > 0);
        Assert.True(fixture.Logger.Count > 0);
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        Assert.Equal("disabled", fixture.Bot.Status.State);
        Assert.False(WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).CommandBots!.TelegramEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ReceiveIgnoresBotSendersAndKeepsUserMessages(bool? isBot)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.WaitForState("connected");
        var received = CommandBotTestObserver.NextCommandAsync(fixture.Logger);
        await fixture.Updates.Writer.WriteAsync(new[]
        {
            Update(10, 123, "/系统状态", true),
            Update(11, 123, "/帮助", isBot)
        });
        Assert.Equal(1, await received.WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        Assert.Equal(1, fixture.Logger.Count);
        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Message.Contains("系统状态", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidCredentialsStopAndCanDisableWithoutValidation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.ErrorCode = 401;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.WaitForState("failed");
        Assert.True(fixture.Bot.Status.Enabled);
        Assert.DoesNotContain("123:mock", fixture.Bot.Status.Error!);
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, settings => settings with { TelegramBotToken = "456:new" }, CancellationToken.None);
        await fixture.WaitForState("failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile,
            settings => settings with { TelegramBotToken = "" }, CancellationToken.None));
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        await fixture.Configuration.SaveNotificationsAsync(fixture.Paths.ConfigurationFile, settings => settings with { TelegramBotToken = "" }, CancellationToken.None);
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        Assert.False(fixture.Bot.Status.Enabled);
    }

    [Fact]
    public async Task ConflictingReceiverDoesNotRetryOrRemoveWebhook()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.ErrorCode = 409;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.WaitForState("failed");
        Assert.Contains("占用", fixture.Bot.Status.Error!);
        Assert.DoesNotContain("deleteWebhook", fixture.Methods);
    }

    [Fact]
    public async Task TemporaryFailureReconnectsAndStopReleasesReceiver()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.ErrorCode = 503;
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.WaitForState("reconnecting");
        fixture.ErrorCode = 0;
        await fixture.WaitForState("connected");
        await fixture.Bot.SetEnabledAsync(false, CancellationToken.None);
        Assert.Equal("disabled", fixture.Bot.Status.State);
    }

    [Fact]
    public async Task OtherSettingsSavesPreserveBotAndStartupRestoresEnabledState()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Bot.SetEnabledAsync(true, CancellationToken.None);
        await fixture.WaitForState("connected");
        WinPilotSettings.Update(fixture.Paths.ConfigurationFile, current => current with { Port = 9124 });
        Assert.True(WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).CommandBots!.TelegramEnabled);
        await fixture.Bot.StopAsync(CancellationToken.None);
        using var configuration = new CommandBotConfiguration();
        using var restarted = new TelegramCommandBotService(fixture.Paths, NullLogger<TelegramCommandBotService>.Instance, configuration);
        await restarted.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => restarted.Status.State == "connected");
            Assert.True(restarted.Status.Enabled);
        }
        finally { await restarted.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("", "123", "", false)]
    [InlineData("123:mock", "", "", false)]
    [InlineData("123:mock", "not-id", "", false)]
    [InlineData("123:mock", "123", "invalid", true)]
    public void MissingSharedConfigurationFailsValidation(string token, string chat, string proxy, bool proxyEnabled)
    {
        Assert.Throws<InvalidOperationException>(() => TelegramCommandBotService.Validate(new NotificationSettings
        { TelegramBotToken = token, TelegramChatId = chat, TelegramProxyUrl = proxy, TelegramProxyEnabled = proxyEnabled }));
    }

    [Theory]
    [InlineData("", "https://api.telegram.org/bot123:mock")]
    [InlineData("https://api.telegram.org/bot", "https://api.telegram.org/bot123:mock")]
    [InlineData("https://example.test/", "https://example.test/bot123:mock")]
    public void SharedTransportSupportsDefaultAndCustomApi(string api, string expected)
    {
        Assert.Equal(expected, TelegramTransport.GetBotUrl(new NotificationSettings { TelegramBotToken = "123:mock", TelegramApiBaseUrl = api }));
    }

    private static object Update(long id, long chat, string text, bool? isBot = null) => new
    {
        update_id = id, message = new
        {
            chat = new { id = chat },
            from = isBot is { } bot ? (object)new { first_name = "测试用户", is_bot = bot } : new { first_name = "测试用户" },
            text
        }
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly CommandBotConfiguration configuration = new();
        public CommandBotConfiguration Configuration => configuration;
        public int ActivePolls;
        public PortableDataPaths Paths { get; }
        public CommandBotTestObserver<TelegramCommandBotService> Logger { get; } = new();
        public TelegramCommandBotService Bot { get; }
        public Channel<object[]> Updates { get; } = Channel.CreateUnbounded<object[]>();
        public System.Collections.Concurrent.ConcurrentBag<string> Methods { get; } = [];
        public volatile int ErrorCode;

        private Fixture(WebApplication app, string directory)
        {
            this.app = app;
            Paths = PortableDataPaths.Create(directory);
            Bot = new(Paths, Logger, configuration);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var fixture = new Fixture(app, Path.Combine(Path.GetTempPath(), "winpilot-command-bot-tests", Guid.NewGuid().ToString("N")));
            app.MapPost("/bot{token}/{method}", async (HttpContext context, string method) =>
            {
                fixture.Methods.Add(method);
                if (fixture.ErrorCode != 0) return Results.StatusCode(fixture.ErrorCode);
                if (method == "getMe") return Results.Json(new { ok = true, result = new { id = 1, username = "test_bot" } });
                if (method == "getWebhookInfo") return Results.Json(new { ok = true, result = new { url = "" } });
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (document.RootElement.GetProperty("offset").GetInt64() == -1)
                    return Results.Json(new { ok = true, result = new[] { Update(9, 123, "积压消息") } });
                Interlocked.Increment(ref fixture.ActivePolls);
                try
                {
                    var batch = await fixture.Updates.Reader.ReadAsync(context.RequestAborted);
                    return Results.Json(new { ok = true, result = batch });
                }
                finally { Interlocked.Decrement(ref fixture.ActivePolls); }
            });
            await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            new WinPilotSettings { Port = 9123, Notifications = new NotificationSettings
            { TelegramBotToken = "123:mock", TelegramChatId = "123", TelegramApiBaseUrl = url }, CommandBots = new() { TelegramPrefix = "/" } }.Save(fixture.Paths.ConfigurationFile);
            return fixture;
        }

        public Task WaitForState(string state) => WaitUntil(() => Bot.Status.State == state);

        public async ValueTask DisposeAsync()
        {
            await Bot.StopAsync(CancellationToken.None);
            Bot.Dispose();
            configuration.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(Paths.ApplicationDirectory, recursive: true);
        }
    }
}
