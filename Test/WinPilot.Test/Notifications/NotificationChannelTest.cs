using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class NotificationChannelTest
{
    [Theory]
    [InlineData("onebot", "unknown", "text", "OneBot 发送目标无效")]
    [InlineData("qqbot", "unknown", "text", "QQBot 发送目标无效")]
    [InlineData("qqbot", "auto", "unknown", "QQBot 消息格式无效")]
    [InlineData("onebot", "private", "text", "OneBot 未配置私聊发送对象")]
    [InlineData("qqbot", "group", "text", "QQBot 未配置群聊发送对象")]
    [InlineData("onebot", "auto", "text", null)]
    [InlineData("qqbot", "auto", "markdown", null)]
    public async Task SaveEndpointPreservesValueAndRecipientValidation(string channel, string target, string format, string? error)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Notification.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile);
            var original = File.ReadAllText(paths.ConfigurationFile);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<CommandBotConfiguration>();
            await using var app = builder.Build();
            app.MapNotificationEndpoints(paths.ConfigurationFile, new EndpointNotifications());
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/settings/notifications/{channel}")
            {
                Content = JsonContent.Create(new NotificationSettings
                {
                    OneBotSendTarget = target, QQBotSendTarget = target, QQBotMessageFormat = format
                })
            };
            using var response = await client.SendAsync(request);
            Assert.Equal(error is null ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
            if (error is not null)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(error, json.RootElement.GetProperty("message").GetString());
                Assert.Equal(original, File.ReadAllText(paths.ConfigurationFile));
            }
            else
            {
                var saved = WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).Notifications;
                Assert.Equal(target, channel == "onebot" ? saved.OneBotSendTarget : saved.QQBotSendTarget);
                if (channel == "qqbot") Assert.Equal(format, saved.QQBotMessageFormat);
                Assert.False(saved.OneBotEnabled);
                Assert.False(saved.QQBotEnabled);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(null, "请选择通知渠道")]
    [InlineData(" ", "请选择通知渠道")]
    [InlineData("unknown", "通知渠道无效")]
    [InlineData("all-enabled", "通知渠道无效")]
    [InlineData(" Windows ", "通知渠道无效")]
    public async Task TestEndpointRejectsInvalidChannelsBeforeSending(string? channel, string error)
    {
        var notifications = new EndpointNotifications();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<CommandBotConfiguration>();
        await using var app = builder.Build();
        app.MapNotificationEndpoints("unused.json", notifications);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/notifications/test", new { channel, title = "test", content = "content" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (string.IsNullOrWhiteSpace(channel))
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(error, json.RootElement.GetProperty("errors").GetProperty("channel")[0].GetString());
        }
        else Assert.Equal(error, json.RootElement.GetProperty("message").GetString());
        Assert.Equal(0, notifications.Calls);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("WEBHOOK")]
    [InlineData("telegram")]
    [InlineData("WORKWEIXIN")]
    [InlineData("feishu")]
    [InlineData("ONEBOT")]
    [InlineData("qqbot")]
    public async Task TestEndpointAcceptsCatalogChannelsIgnoringCaseAndPreservesSuccessfulResponse(string channel)
    {
        var notifications = new EndpointNotifications { Results = [new(channel, true)] };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<CommandBotConfiguration>();
        await using var app = builder.Build();
        app.MapNotificationEndpoints("unused.json", notifications);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/notifications/test", new { channel, title = "test", content = "content" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(channel, Assert.Single(json.RootElement.EnumerateArray()).GetProperty("channel").GetString());
        Assert.True(json.RootElement[0].GetProperty("succeeded").GetBoolean());
        Assert.Equal(1, notifications.Calls);
        Assert.Equal(channel, notifications.Channel);
        Assert.True(notifications.IgnoreChannelEnabled);
        Assert.Equal(new NotificationMessage("test", "content"), notifications.Message);
    }

    [Fact]
    public async Task TestEndpointDistinguishesValidChannelWithNoAvailableDelivery()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<CommandBotConfiguration>();
        var notifications = new EndpointNotifications();
        await using var app = builder.Build();
        app.MapNotificationEndpoints("unused.json", notifications);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/notifications/test", new { channel = "Windows", title = "test", content = "content" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("没有可用的通知渠道", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(1, notifications.Calls);
    }

    [Fact]
    public async Task SaveEndpointPropagatesCancellationWhileWaitingForConfigurationGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Notification.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<CommandBotConfiguration>();
            await using var app = builder.Build();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Use(async (context, next) =>
            {
                var handling = next(context);
                entered.TrySetResult();
                try { await handling; completed.TrySetResult(null); }
                catch (Exception exception) { completed.TrySetResult(exception); throw; }
            });
            app.MapNotificationEndpoints(paths.ConfigurationFile, new EndpointNotifications());
            await app.StartAsync();
            var configuration = app.Services.GetRequiredService<CommandBotConfiguration>();
            await configuration.Gate.WaitAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/settings/notifications/windows")
            {
                Content = JsonContent.Create(new NotificationSettings { WindowsEnabled = true })
            };
            var sending = client.SendAsync(request, cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(cancellation.Token);
                Assert.False(completed.Task.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
                Assert.IsAssignableFrom<OperationCanceledException>(await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).Notifications.WindowsEnabled);
            }
            finally { cancellation.Cancel(); configuration.Gate.Release(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QQBotBindingStreamDisablesCachingAndPreservesEvents(bool group)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinPilot.Notification.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(root);
            WinPilotSettings.Update(paths.ConfigurationFile, current => current with
            {
                Notifications = current.Notifications with { QQBotAppId = "app", QQBotClientSecret = "secret" }
            });
            using var configuration = new CommandBotConfiguration();
            await using var bot = new QQBotCommandBotService(paths, NullLogger<QQBotCommandBotService>.Instance, configuration,
                (_, _, _, _, _) => throw new InvalidOperationException("不应建立真实连接"),
                async (_, receivedGroup, report, token) =>
                {
                    Assert.Equal(group, receivedGroup);
                    await report("test-code", token);
                    return "test-openid";
                });
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(bot);
            await using var app = builder.Build();
            app.MapNotificationEndpoints(paths.ConfigurationFile, new EndpointNotifications());
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var response = await client.PostAsync($"/api/notifications/qqbot/bind-{(group ? "group" : "user")}", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoCache);
            Assert.Equal(new[] { "data: {\"type\":\"code\",\"code\":\"test-code\"}", "data: {\"type\":\"success\",\"openId\":\"test-openid\"}" },
                (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class EndpointNotifications : INotificationService
    {
        public IReadOnlyList<NotificationChannelResult> Results { get; init; } = [];
        public int Calls { get; private set; }
        public string? Channel { get; private set; }
        public NotificationMessage? Message { get; private set; }
        public bool IgnoreChannelEnabled { get; private set; }
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default,
            string? channel = null, bool ignoreChannelEnabled = false)
        {
            Calls++;
            Channel = channel;
            Message = message;
            IgnoreChannelEnabled = ignoreChannelEnabled;
            return Task.FromResult(Results);
        }
    }

    [Fact]
    public async Task ChannelOptionsEndpointUsesCatalogWithoutInitializingSettings()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<CommandBotConfiguration>();
        using var notifications = new NotificationService(paths, NullLogger<NotificationService>.Instance);
        await using var app = builder.Build();
        app.MapNotificationEndpoints(paths.ConfigurationFile, notifications);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/notifications/channels");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var options = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal("", options[0].GetProperty("value").GetString());
        Assert.Equal(NotificationChannelCatalog.EnabledDisplayName, options[0].GetProperty("label").GetString());
        Assert.Equal(NotificationChannelCatalog.All.Select(channel => (channel.Id, channel.DisplayName)),
            options.Skip(1).Select(option => (option.GetProperty("value").GetString()!, option.GetProperty("label").GetString()!)));
        Assert.False(File.Exists(paths.ConfigurationFile));
    }

    [Theory]
    [InlineData("windows", "Windows 通知")]
    [InlineData("WorkWeixin", "企业微信")]
    [InlineData("QQBOT", "QQBot")]
    [InlineData("unknown", "unknown")]
    public void ChannelDisplayNamesIgnoreIdCasingAndPreserveUnknownIds(string id, string displayName)
    {
        Assert.Equal(displayName, NotificationChannelCatalog.GetDisplayName(id));
    }

    [Fact]
    public async Task TransportTimeoutIsReportedAsFailureButCallerCancellationPropagates()
    {
        using var service = new NotificationService(
            PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            NullLogger<NotificationService>.Instance);
        Func<Task> timeout = () => Task.FromException(new OperationCanceledException());
        var result = await service.SendChannel("Webhook", timeout, CancellationToken.None);
        Assert.False(result.Succeeded);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendChannel("Webhook", timeout, cancellation.Token));
    }

    [Theory]
    [InlineData("auto", "user", "", "private")]
    [InlineData("auto", "", "group", "group")]
    [InlineData("auto", "user", "group", "private,group")]
    [InlineData("private", "user", "group", "private")]
    [InlineData("group", "user", "group", "group")]
    public void SendTargetsSelectOnlyConfiguredAndAllowedTargets(string mode, string user, string group, string expected)
    {
        var settings = new NotificationSettings
        {
            OneBotSendTarget = mode, OneBotUserId = user, OneBotGroupId = group,
            QQBotSendTarget = mode, QQBotOpenId = user, QQBotGroupOpenId = group
        };
        Assert.Equal(expected, string.Join(",", OneBotNotificationChannel.SelectTargets(settings).Select(target => target.Type)));
        Assert.Equal(expected, string.Join(",", QQBotNotificationChannel.SelectTargets(settings).Select(target => target.Type)));
    }

    [Theory]
    [InlineData("auto", "", "")]
    [InlineData("private", "", "group")]
    [InlineData("group", "user", "")]
    [InlineData("invalid", "user", "group")]
    public void SendTargetsRejectMissingSelectedTargetAndUnknownMode(string mode, string user, string group)
    {
        var settings = new NotificationSettings
        {
            OneBotSendTarget = mode, OneBotUserId = user, OneBotGroupId = group,
            QQBotSendTarget = mode, QQBotOpenId = user, QQBotGroupOpenId = group
        };
        Assert.Throws<InvalidOperationException>(() => OneBotNotificationChannel.SelectTargets(settings));
        Assert.Throws<InvalidOperationException>(() => QQBotNotificationChannel.SelectTargets(settings));
    }

    [Theory]
    [InlineData("private")]
    [InlineData("group")]
    public async Task SelectedTargetIsUsedByBothChannels(string mode)
    {
        using var handler = new RecordingHandler(request => request.Uri.Host == "bots.qq.com"
            ? "{\"access_token\":\"token\",\"expires_in\":3600}" : "{\"status\":\"ok\"}");
        using var client = new HttpClient(handler);
        await new OneBotNotificationChannel(client).SendAsync(new()
        {
            OneBotEndpoint = "https://onebot.test", OneBotUserId = "user", OneBotGroupId = "group", OneBotSendTarget = mode
        }, new("标题", "正文"), CancellationToken.None);
        using var qqbot = new QQBotNotificationChannel(client);
        await qqbot.SendAsync(new()
        {
            QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "user", QQBotGroupOpenId = "group", QQBotSendTarget = mode
        }, new("标题", "正文"), CancellationToken.None);
        var requests = handler.Requests.ToArray();
        var onebot = Assert.Single(requests.Where(request => request.Uri.Host == "onebot.test"));
        using var payload = JsonDocument.Parse(onebot.Body);
        Assert.Equal(mode, payload.RootElement.GetProperty("message_type").GetString());
        var qq = Assert.Single(requests.Where(request => request.Uri.Host == "api.sgroup.qq.com"));
        Assert.Equal(mode == "private" ? "/v2/users/user/messages" : "/v2/groups/group/messages", qq.Uri.AbsolutePath);
    }

    [Fact]
    public void QQBotJsonFieldsFollowApiAndStorageNaming()
    {
        var settings = new NotificationSettings { QQBotEnabled = true, QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "user", QQBotGroupOpenId = "group" };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(json.RootElement.GetProperty("qqBotEnabled").GetBoolean());
        Assert.Equal("app", json.RootElement.GetProperty("qqBotAppId").GetString());
        Assert.Equal("secret", json.RootElement.GetProperty("qqBotClientSecret").GetString());
        Assert.Equal("user", json.RootElement.GetProperty("qqBotOpenId").GetString());
        Assert.Equal("group", json.RootElement.GetProperty("qqBotGroupOpenId").GetString());
        Assert.Equal("text", json.RootElement.GetProperty("qqBotMessageFormat").GetString());
        Assert.Equal(settings.QQBotSendTarget, json.RootElement.GetProperty("qqBotSendTarget").GetString());
        var submitted = JsonSerializer.Deserialize<NotificationSettings>(json.RootElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(settings, submitted);
        using var data = new TestData();
        new WinPilotSettings { Port = 9123, Notifications = settings }.Save(data.Paths.ConfigurationFile);
        using var saved = JsonDocument.Parse(File.ReadAllText(data.Paths.ConfigurationFile));
        var notifications = saved.RootElement.GetProperty("notifications");
        Assert.True(notifications.GetProperty("QQBotEnabled").GetBoolean());
        Assert.Equal("app", notifications.GetProperty("QQBotAppId").GetString());
        Assert.Equal("secret", notifications.GetProperty("QQBotClientSecret").GetString());
        Assert.Equal("user", notifications.GetProperty("QQBotOpenId").GetString());
        Assert.Equal("group", notifications.GetProperty("QQBotGroupOpenId").GetString());
        Assert.Equal("text", notifications.GetProperty("QQBotMessageFormat").GetString());
        Assert.Equal(settings.QQBotSendTarget, notifications.GetProperty("QQBotSendTarget").GetString());
    }

    [Theory]
    [InlineData("text", 0)]
    [InlineData("markdown", 2)]
    public async Task QQBotNotificationsAndRepliesShareTextFormat(string format, int messageType)
    {
        using var handler = new RecordingHandler(request => request.Uri.Host == "bots.qq.com"
            ? "{\"access_token\":\"token\",\"expires_in\":3600}" : "{}");
        using var client = new HttpClient(handler);
        using var channel = new QQBotNotificationChannel(client);
        var settings = new NotificationSettings
        {
            QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "user", QQBotMessageFormat = format
        };
        await channel.SendAsync(settings, new("标题", "**正文**"), CancellationToken.None);
        await channel.ReplyAsync(settings, new("group", "sender", "帮助") { ConversationType = "group", MessageId = "source" },
            "**回复**", null, 3, CancellationToken.None);
        var messages = handler.Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/messages")).ToArray();
        Assert.Equal(2, messages.Length);
        for (var index = 0; index < messages.Length; index++)
        {
            using var json = JsonDocument.Parse(messages[index].Body);
            var payload = json.RootElement;
            Assert.Equal(messageType, payload.GetProperty("msg_type").GetInt32());
            var text = format == "markdown" ? payload.GetProperty("markdown").GetProperty("content").GetString()
                : payload.GetProperty("content").GetString();
            Assert.Contains(index == 0 ? "**正文**" : "**回复**", text);
            Assert.False(payload.TryGetProperty(format == "markdown" ? "content" : "markdown", out _));
            if (index == 1)
            {
                Assert.Equal("source", payload.GetProperty("msg_id").GetString());
                Assert.Equal(3, payload.GetProperty("msg_seq").GetInt32());
            }
        }
    }

    [Theory]
    [InlineData("text", null)]
    [InlineData("text", "")]
    [InlineData("text", " \t")]
    [InlineData("markdown", null)]
    [InlineData("markdown", "")]
    [InlineData("markdown", " \t")]
    public async Task QQBotSendsTitleOnlyNotifications(string format, string? content)
    {
        using var handler = new RecordingHandler(request => request.Uri.Host == "bots.qq.com"
            ? "{\"access_token\":\"token\",\"expires_in\":3600}" : "{}");
        using var client = new HttpClient(handler);
        using var channel = new QQBotNotificationChannel(client);
        await channel.SendAsync(new()
        {
            QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "user", QQBotMessageFormat = format
        }, new("这是一条测试通知信息", content!), CancellationToken.None);
        var message = Assert.Single(handler.Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/messages")));
        using var json = JsonDocument.Parse(message.Body);
        Assert.Equal(format == "markdown" ? 2 : 0, json.RootElement.GetProperty("msg_type").GetInt32());
        var text = format == "markdown" ? json.RootElement.GetProperty("markdown").GetProperty("content").GetString()
            : json.RootElement.GetProperty("content").GetString();
        Assert.Equal("这是一条测试通知信息", text);
    }

    [Fact]
    public void QQBotMessageFormatDefaultsForExistingConfigurationAndRejectsUnknownValues()
    {
        Assert.Equal("text", JsonSerializer.Deserialize<NotificationSettings>("{}")!.QQBotMessageFormat);
        Assert.Throws<InvalidOperationException>(() => QQBotNotificationChannel.ValidateMessageFormat(new() { QQBotMessageFormat = "invalid" }));
    }

    [Fact]
    public async Task QQBotRefreshesCacheWhenAppIdOrSecretChanges()
    {
        using var handler = new RecordingHandler(request =>
        {
            if (request.Uri.Host != "bots.qq.com") return "{}";
            using var credentials = JsonDocument.Parse(request.Body);
            var root = credentials.RootElement;
            return JsonSerializer.Serialize(new { access_token = root.GetProperty("appId").GetString() + ":" + root.GetProperty("clientSecret").GetString(), expires_in = 3600 });
        });
        using var client = new HttpClient(handler);
        using var channel = new QQBotNotificationChannel(client);
        var settings = new NotificationSettings { QQBotAppId = "first", QQBotClientSecret = "secret", QQBotOpenId = "user" };
        await channel.SendAsync(settings, new("标题", "正文"), CancellationToken.None);
        await channel.SendAsync(settings with { QQBotAppId = "second" }, new("标题", "正文"), CancellationToken.None);
        await channel.SendAsync(settings with { QQBotAppId = "second", QQBotClientSecret = "new-secret" }, new("标题", "正文"), CancellationToken.None);
        await channel.SendAsync(settings with { QQBotAppId = "second", QQBotClientSecret = "new-secret" }, new("标题", "正文"), CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count(request => request.Uri.Host == "bots.qq.com"));
        Assert.Equal(new[] { "QQBot first:secret", "QQBot second:secret", "QQBot second:new-secret", "QQBot second:new-secret" },
            handler.Requests.Where(request => request.Uri.Host == "api.sgroup.qq.com").Select(request => request.Authorization));
    }

    [Fact]
    public async Task QQBotDoesNotFallBackToOldTokenWhenChangedCredentialsFail()
    {
        using var handler = new RecordingHandler(request => request.Uri.Host != "bots.qq.com" ? "{}"
            : request.Body.Contains("invalid") ? "{\"access_token\":null}" : "{\"access_token\":\"valid-token\",\"expires_in\":3600}");
        using var client = new HttpClient(handler);
        using var channel = new QQBotNotificationChannel(client);
        var settings = new NotificationSettings { QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "user" };
        await channel.SendAsync(settings, new("标题", "正文"), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync(settings with { QQBotClientSecret = "invalid" }, new("标题", "正文"), CancellationToken.None));
        Assert.Single(handler.Requests.Where(request => request.Uri.Host == "api.sgroup.qq.com"));
        await channel.SendAsync(settings, new("标题", "正文"), CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count(request => request.Uri.Host == "bots.qq.com"));
    }

    [Fact]
    public async Task QQBotDoesNotCacheAlreadyExpiredTokens()
    {
        using var handler = new RecordingHandler(request => request.Uri.Host == "bots.qq.com"
            ? "{\"access_token\":\"expired-token\",\"expires_in\":0}" : "{}");
        using var client = new HttpClient(handler);
        using var channel = new QQBotNotificationChannel(client);
        var settings = new NotificationSettings { QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "user" };
        await channel.SendAsync(settings, new("标题", "正文"), CancellationToken.None);
        await channel.SendAsync(settings, new("标题", "正文"), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.Host == "bots.qq.com"));
    }

    [Fact]
    public async Task RoutingPreservesEnabledSelectionResultOrderAndFailureIsolation()
    {
        await using var server = await LocalServer.CreateAsync();
        using var data = new TestData();
        var settings = new NotificationSettings
        {
            WebhookEnabled = true, WebhookUrl = server.Url + "/failure",
            TelegramEnabled = true, TelegramApiBaseUrl = server.Url, TelegramBotToken = "123:test", TelegramChatId = "42",
            FeishuEnabled = true, FeishuWebhookUrl = server.Url + "/feishu",
            OneBotEnabled = true, OneBotEndpoint = server.Url, OneBotUserId = "42"
        };
        new WinPilotSettings { Port = 9123, Notifications = settings }.Save(data.Paths.ConfigurationFile);
        using var service = new NotificationService(data.Paths, NullLogger<NotificationService>.Instance);
        var results = await service.SendAsync(new("标题", "正文"));
        Assert.Equal(new[] { "Webhook", "Telegram", "飞书", "OneBot" }, results.Select(result => result.Channel));
        Assert.False(results[0].Succeeded);
        Assert.All(results.Skip(1), result => Assert.True(result.Succeeded));
        Assert.Single(await service.SendAsync(new("标题", "正文"), channel: "TELEGRAM"));
        new WinPilotSettings { Port = 9123, Notifications = settings with { TelegramEnabled = false } }.Save(data.Paths.ConfigurationFile);
        Assert.Empty(await service.SendAsync(new("标题", "正文"), channel: "telegram"));
        Assert.True(Assert.Single(await service.SendAsync(new("标题", "正文"), channel: "telegram", ignoreChannelEnabled: true)).Succeeded);
        Assert.Empty(await service.SendAsync(new("标题", "正文"), channel: "unknown"));
    }

    [Fact]
    public async Task CancellationPropagatesWithoutSendingWindowsNotification()
    {
        using var data = new TestData();
        using var service = new NotificationService(data.Paths, NullLogger<NotificationService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendAsync(
            new("标题", "正文"), cancellation.Token, "windows", ignoreChannelEnabled: true));
    }

    [Fact]
    public async Task WebhookPreservesMetadataAndJpegScreenshot()
    {
        using var data = new TestData();
        using var handler = new RecordingHandler(_ => "{}");
        using var client = new HttpClient(handler);
        await new WebhookNotificationChannel(client).SendAsync(new() { WebhookUrl = "https://example.test/hook", WebhookSendTo = "owner" },
            new("标题", "正文", data.Image, "Test.Event", "Failure", "2026-09-30T00:00:00Z", new { value = 7 }), CancellationToken.None);
        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var payload = document.RootElement;
        Assert.Equal("标题\n正文", payload.GetProperty("message").GetString());
        Assert.Equal("owner", payload.GetProperty("send_to").GetString());
        Assert.Equal("Test.Event", payload.GetProperty("event").GetString());
        Assert.Equal("Failure", payload.GetProperty("result").GetString());
        Assert.Equal("2026-09-30T00:00:00Z", payload.GetProperty("timestamp").GetString());
        Assert.Equal(7, payload.GetProperty("data").GetProperty("value").GetInt32());
        var image = Convert.FromBase64String(payload.GetProperty("screenshot").GetString()!);
        Assert.Equal(new byte[] { 0xff, 0xd8 }, image.Take(2));
    }

    [Fact]
    public async Task OneBotPreservesPrivateAndGroupTargetsAuthorizationAndPngImage()
    {
        using var data = new TestData();
        using var handler = new RecordingHandler(_ => "{\"status\":\"ok\"}");
        using var client = new HttpClient(handler);
        await new OneBotNotificationChannel(client).SendAsync(new()
        { OneBotEndpoint = "https://example.test/send_msg/", OneBotUserId = "123", OneBotGroupId = "456", OneBotToken = "secret" },
            new("标题", "正文", data.Image), CancellationToken.None);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request => { Assert.Equal("/send_msg", request.Uri.AbsolutePath); Assert.Equal("Bearer secret", request.Authorization); });
        using var user = JsonDocument.Parse(requests[0].Body);
        using var group = JsonDocument.Parse(requests[1].Body);
        Assert.Equal("private", user.RootElement.GetProperty("message_type").GetString());
        Assert.Equal("123", user.RootElement.GetProperty("user_id").GetString());
        Assert.Equal("group", group.RootElement.GetProperty("message_type").GetString());
        Assert.Equal("456", group.RootElement.GetProperty("group_id").GetString());
        var segments = user.RootElement.GetProperty("message");
        Assert.Equal("标题\n正文", segments[0].GetProperty("data").GetProperty("text").GetString());
        var image = Convert.FromBase64String(segments[1].GetProperty("data").GetProperty("file").GetString()![9..]);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, image.Take(4));
    }

    [Fact]
    public async Task OneBotReportsProtocolFailure()
    {
        using var handler = new RecordingHandler(_ => "{\"status\":\"failed\"}");
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OneBotNotificationChannel(client).SendAsync(
            new() { OneBotEndpoint = "https://example.test", OneBotUserId = "123" }, new("标题", "正文"), CancellationToken.None));
    }

    [Fact]
    public async Task WorkWeixinSendsJpegWithMatchingMd5BeforeText()
    {
        using var data = new TestData();
        using var handler = new RecordingHandler(_ => "{}");
        using var client = new HttpClient(handler);
        await new WorkWeixinNotificationChannel(client).SendAsync(new() { WorkWeixinWebhookUrl = "https://example.test/hook" },
            new("标题", "正文", data.Image), CancellationToken.None);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        using var imageDocument = JsonDocument.Parse(requests[0].Body);
        using var textDocument = JsonDocument.Parse(requests[1].Body);
        Assert.Equal("image", imageDocument.RootElement.GetProperty("msgtype").GetString());
        var image = imageDocument.RootElement.GetProperty("image");
        var bytes = Convert.FromBase64String(image.GetProperty("base64").GetString()!);
        Assert.Equal(Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(), image.GetProperty("md5").GetString());
        Assert.Equal("text", textDocument.RootElement.GetProperty("msgtype").GetString());
        Assert.Equal("标题\n正文", textDocument.RootElement.GetProperty("text").GetProperty("content").GetString());
    }

    [Fact]
    public async Task FeishuAuthenticatesUploadsImageAndSendsPost()
    {
        using var data = new TestData();
        using var handler = new RecordingHandler(request => request.Uri.AbsolutePath switch
        {
            "/open-apis/auth/v3/tenant_access_token/internal" => "{\"tenant_access_token\":\"feishu-access\"}",
            "/open-apis/im/v1/images" => "{\"data\":{\"image_key\":\"image-key\"}}",
            _ => "{}"
        });
        using var client = new HttpClient(handler);
        using var sender = new FeishuNotificationChannel(client);
        await sender.SendAsync(new()
        { FeishuWebhookUrl = "https://example.test/hook", FeishuAppId = "app", FeishuAppSecret = "secret" },
            new("标题", "正文", data.Image), CancellationToken.None);
        var requests = handler.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        using var auth = JsonDocument.Parse(requests[0].Body);
        Assert.Equal("app", auth.RootElement.GetProperty("app_id").GetString());
        Assert.Equal("secret", auth.RootElement.GetProperty("app_secret").GetString());
        Assert.Equal("Bearer feishu-access", requests[1].Authorization);
        Assert.Contains("image.png", requests[1].Body);
        using var post = JsonDocument.Parse(requests[2].Body);
        Assert.Equal("post", post.RootElement.GetProperty("msg_type").GetString());
        var content = post.RootElement.GetProperty("content").GetProperty("post").GetProperty("zh_cn").GetProperty("content")[0];
        Assert.Equal("标题\n正文", content[0].GetProperty("text").GetString());
        Assert.Equal("image-key", content[1].GetProperty("image_key").GetString());
    }

    [Fact]
    public async Task QQBotCachesTokenAcrossConcurrentSendsAndKeepsBothTargets()
    {
        using var handler = new RecordingHandler(request => request.Uri.Host == "bots.qq.com"
            ? "{\"access_token\":\"qq-access\",\"expires_in\":\"3600\"}" : "{}");
        using var client = new HttpClient(handler);
        using var channel = new QQBotNotificationChannel(client);
        var settings = new NotificationSettings { QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "user", QQBotGroupOpenId = "group" };
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => channel.SendAsync(settings, new("标题", "正文"), CancellationToken.None)));
        Assert.Single(handler.Requests.Where(request => request.Uri.Host == "bots.qq.com"));
        var messages = handler.Requests.Where(request => request.Uri.Host == "api.sgroup.qq.com").ToArray();
        Assert.Equal(6, messages.Length);
        Assert.Equal(3, messages.Count(request => request.Uri.AbsolutePath == "/v2/users/user/messages"));
        Assert.Equal(3, messages.Count(request => request.Uri.AbsolutePath == "/v2/groups/group/messages"));
        Assert.All(messages, request => Assert.Equal("QQBot qq-access", request.Authorization));
    }

    [Theory]
    [InlineData("text", "正文")]
    [InlineData("markdown", "正文")]
    [InlineData("text", "")]
    [InlineData("markdown", null)]
    public async Task QQBotPreservesPreparePartFinishMergeAndImageMessage(string format, string? content)
    {
        using var data = new TestData();
        using var handler = new RecordingHandler(request => request.Uri.AbsolutePath switch
        {
            "/app/getAppAccessToken" => "{\"access_token\":\"qq-access\",\"expires_in\":3600}",
            "/v2/users/user/upload_prepare" => "{\"upload_id\":\"upload\",\"block_size\":\"100000\",\"parts\":[{\"index\":1,\"presigned_url\":\"https://upload.test/part\"}]}",
            "/v2/users/user/files" => "{\"file_info\":\"file-info\"}",
            _ => "{}"
        });
        using var client = new HttpClient(handler);
        using var channel = new QQBotNotificationChannel(client);
        await channel.SendAsync(new() { QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "user", QQBotMessageFormat = format },
            new("标题", content!, data.Image), CancellationToken.None);
        var requests = handler.Requests.ToArray();
        var expected = new List<string> { "/app/getAppAccessToken" };
        if (!string.IsNullOrWhiteSpace(content)) expected.Add("/v2/users/user/messages");
        expected.AddRange(["/v2/users/user/upload_prepare", "/part", "/v2/users/user/upload_part_finish", "/v2/users/user/files", "/v2/users/user/messages"]);
        Assert.Equal(expected,
            requests.Select(request => request.Uri.AbsolutePath));
        var upload = Assert.Single(requests.Where(request => request.Uri.Host == "upload.test"));
        Assert.Equal(HttpMethod.Put, upload.Method);
        Assert.Null(upload.Authorization);
        using var image = JsonDocument.Parse(requests[^1].Body);
        Assert.Equal(7, image.RootElement.GetProperty("msg_type").GetInt32());
        Assert.False(image.RootElement.TryGetProperty("markdown", out _));
        Assert.Equal("file-info", image.RootElement.GetProperty("media").GetProperty("file_info").GetString());
    }

    [Fact]
    public async Task TelegramPreservesMultipartPhotoAndLongTextFallback()
    {
        await using var server = await LocalServer.CreateAsync();
        using var data = new TestData();
        await TelegramNotificationChannel.SendAsync(new()
        { TelegramBotToken = "123:test", TelegramChatId = "42", TelegramApiBaseUrl = server.Url },
            new("标题", new string('x', 1024), data.Image), CancellationToken.None);
        var requests = server.Requests.ToArray();
        Assert.Equal(new[] { "/bot123:test/sendPhoto", "/bot123:test/sendMessage" }, requests.Select(request => request.Path));
        Assert.Contains("image.png", requests[0].Body);
        Assert.Contains("chat_id", requests[0].Body);
        Assert.Contains("caption", requests[0].Body);
        using var text = JsonDocument.Parse(requests[1].Body);
        Assert.Equal("42", text.RootElement.GetProperty("chat_id").GetString());
        Assert.True(text.RootElement.GetProperty("disable_web_page_preview").GetBoolean());
    }

    private sealed record Request(Uri Uri, HttpMethod Method, string? Authorization, string Body);

    private sealed class RecordingHandler(Func<Request, string> reply) : HttpMessageHandler
    {
        public ConcurrentQueue<Request> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new Request(request.RequestUri!, request.Method, request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Enqueue(captured);
            return new(HttpStatusCode.OK) { Content = new StringContent(reply(captured)) };
        }
    }

    private sealed class TestData : IDisposable
    {
        public PortableDataPaths Paths { get; } = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.NotificationChannel.Test", Guid.NewGuid().ToString("N")));
        public string Image => Path.Combine(Paths.ApplicationDirectory, "source.png");
        public TestData()
        {
            Directory.CreateDirectory(Paths.ApplicationDirectory);
            using var image = new Bitmap(4, 4);
            image.SetPixel(0, 0, Color.Blue);
            image.Save(Image, ImageFormat.Png);
        }
        public void Dispose() => Directory.Delete(Paths.ApplicationDirectory, true);
    }

    private sealed class LocalServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public ConcurrentQueue<(string Path, string Body)> Requests { get; } = new();
        public static async Task<LocalServer> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            LocalServer? server = null;
            app.MapPost("/{**path}", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                var path = context.Request.Path.Value!;
                server!.Requests.Enqueue((path, body));
                return path == "/failure" ? Results.StatusCode(500)
                    : path == "/send_msg" ? Results.Json(new { status = "ok" }) : Results.Json(new { ok = true });
            });
            await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server = new(app, url);
            return server;
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
