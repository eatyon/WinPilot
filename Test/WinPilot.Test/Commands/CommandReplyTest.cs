using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Desktop;
using WinPilot.Core.SystemStatus;
using WinPilot.Features.Notifications;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Notifications.CommandBots.Commands;

namespace WinPilot.Test;

public sealed class CommandReplyTest
{
    [Theory]
    [InlineData("帮助", " 帮助 ", "帮助")]
    [InlineData("启动标签", "启动标签Chrome", "启动标签 Chrome")]
    [InlineData("运行任务", "运行任务 我的 任务", "运行任务 我的 任务")]
    [InlineData("Test配置组", "test配置组\"配置 组1\" 组2", "Test配置组 \"配置 组1\" 组2")]
    [InlineData("插件命令", "插件命令 A\r\nB\tC", "插件命令 A  B C")]
    public void CommandLogsIncludeArgumentsWithoutControlCharacters(string name, string content, string expected)
    {
        Assert.Equal(expected, CommandProcessor.FormatCommandForLog(name, content));
    }

    [Theory]
    [InlineData("帮助", true)]
    [InlineData(" 帮助 ", true)]
    [InlineData("帮助一下", false)]
    [InlineData("/帮助", false)]
    [InlineData("wp帮助", false)]
    [InlineData("WP帮助", false)]
    [InlineData("桌面截图", true)]
    [InlineData(" 桌面截图 ", true)]
    [InlineData("桌面截图一下", false)]
    [InlineData("/桌面截图", false)]
    [InlineData("系统状态", true)]
    [InlineData(" 系统状态 ", true)]
    [InlineData("系统状态一下", false)]
    public void CommandsRequireExactMatch(string command, bool matches) =>
        Assert.Equal(matches, CommandProcessor.IsSupportedCommand(command));

    [Fact]
    public void HelpImageIsPngAndTextUsesSameDefinitionsAndPrefix()
    {
        var bytes = CommandImageRenderer.RenderHelp(CommandProcessor.Definitions, "/");
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        Assert.InRange(image.Width, 1, 8000);
        Assert.InRange(image.Height, 1, 8000);
        Assert.Contains("/帮助", CommandProcessor.HelpText("/"));
        Assert.Contains(CommandProcessor.Definitions[0].Description, CommandProcessor.HelpText("/"));
        Assert.Contains("/桌面截图", CommandProcessor.HelpText("/"));
        var help = CommandProcessor.HelpText("/");
        foreach (var command in CommandProcessor.Definitions)
            Assert.Contains("/" + command.Usage + " — " + command.Description, help);
        Assert.Equal(CommandProcessor.Definitions.Count, CommandProcessor.Definitions.Select(command => command.Name).Distinct().Count());
        Assert.Contains("/启动标签 [标签名]", CommandProcessor.HelpText("/"));
    }

    [Fact]
    public void SystemStatusFormattingPreservesMissingAndZeroValuesAndAllDevices()
    {
        var rows = SystemStatusCommand.FormatRows(new(12.34, 45.6,
            [new("GPU 0", 0), new("GPU 1", null)], [new("磁盘 0", 7.8), new("磁盘 1", double.NaN)]));
        Assert.Equal(new[] { "12.3%", "45.6%", "0.0%", "不可用", "7.8%", "不可用" }, rows.Select(row => row.Description));
        Assert.Equal(new[] { "CPU", "内存", "GPU 0", "GPU 1", "磁盘 0", "磁盘 1" }, rows.Select(row => row.Name));
        var empty = SystemStatusCommand.FormatRows(new(double.NaN, 0, [], []));
        Assert.Equal(new[] { "不可用", "0.0%", "不可用", "不可用" }, empty.Select(row => row.Description));
        Assert.Equal(new[] { "CPU", "内存", "GPU", "磁盘" }, empty.Select(row => row.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemStatusSamplesOnceAndFallsBackToSameValuesIfImageSendFails(bool imageFailure)
    {
        var monitor = new SystemMonitor(() => new(12.3, 45.6, [new("GPU 0", null)], [new("磁盘 0", 0)]));
        using var processor = new CommandProcessor("Test", new(), NullLogger.Instance, systemMonitor: monitor);
        var replies = new List<(string Text, byte[]? Image)>();
        await processor.ProcessAsync(new("origin", "sender", "系统状态")
        {
            Reply = (text, image, _) =>
            {
                replies.Add((text, image));
                return image is not null && imageFailure ? Task.FromException(new IOException()) : Task.CompletedTask;
            }
        }, "", CancellationToken.None);
        Assert.Equal(1, monitor.Captures);
        Assert.Equal(imageFailure ? 2 : 1, replies.Count);
        Assert.Equal("", replies[0].Text);
        using var stream = new MemoryStream(replies[0].Image!);
        using var image = Image.FromStream(stream);
        Assert.InRange(image.Width, 1, 8000);
        if (imageFailure)
        {
            Assert.Null(replies[1].Image);
            Assert.Contains("CPU：12.3%", replies[1].Text);
            Assert.Contains("GPU 0：不可用", replies[1].Text);
            Assert.Contains("磁盘 0：0.0%", replies[1].Text);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    [InlineData(1, 4)]
    public void SystemStatusImageRendersMissingAndMultipleDevices(int gpuCount, int diskCount)
    {
        var status = new SystemResourceStatus(12.3, 45.6,
            Enumerable.Range(0, gpuCount).Select(index => new SystemResourceUsage($"GPU {index}", index == 0 ? 0 : null)).ToArray(),
            Enumerable.Range(0, diskCount).Select(index => new SystemResourceUsage($"磁盘 {index}", 7.8)).ToArray());
        var bytes = CommandImageRenderer.RenderStatus(status);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        using var stream = new MemoryStream(bytes);
        using var image = new Bitmap(stream);
        Assert.InRange(image.Width, 1, 8000);
        Assert.InRange(image.Height, 1, 8000);
        using var baselineStream = new MemoryStream(CommandImageRenderer.RenderStatus(new(0, 0, [], [])));
        using var baseline = Image.FromStream(baselineStream);
        Assert.True(image.Height >= baseline.Height);
    }

    [Fact]
    public void SystemStatusImageRejectsExcessiveDeviceCount()
    {
        var devices = Enumerable.Range(0, 200).Select(index => new SystemResourceUsage($"GPU {index}", 0)).ToArray();
        Assert.Throws<InvalidOperationException>(() => CommandImageRenderer.RenderStatus(new(0, 0, devices, [])));
    }

    [Fact]
    public async Task SystemStatusCaptureFailureRepliesWithoutExceptionDetails()
    {
        var monitor = new SystemMonitor(() => throw new InvalidOperationException("secret-path"));
        using var processor = new CommandProcessor("Test", new(), NullLogger.Instance, systemMonitor: monitor);
        string? received = null;
        await processor.ProcessAsync(new("origin", "sender", "系统状态")
        {
            Reply = (text, image, _) => { Assert.Null(image); received = text; return Task.CompletedTask; }
        }, "", CancellationToken.None);
        Assert.Equal("无法获取系统状态，请稍后重试", received);
        Assert.Equal(1, monitor.Captures);
    }

    [Theory]
    [InlineData("系统状态获取")]
    [InlineData("图片生成")]
    [InlineData("图片发送")]
    public async Task SystemStatusFailuresIdentifyStageAndPreserveAvailableText(string stage)
    {
        var monitor = new SystemMonitor(() => stage == "系统状态获取"
            ? throw new IOException("secret-path")
            : new(12.3, 45.6, stage == "图片生成"
                ? Enumerable.Range(0, 200).Select(index => new SystemResourceUsage($"GPU {index}", 0)).ToArray()
                : [], []));
        var logger = new CommandBotTestObserver();
        using var processor = new CommandProcessor("Test", new() { TelegramBotToken = "secret-token", QQBotClientSecret = "secret-path" }, logger, systemMonitor: monitor);
        var replies = new List<(string Text, byte[]? Image)>();
        await processor.ProcessAsync(new("origin", "sender", "系统状态")
        {
            Reply = (text, image, _) =>
            {
                replies.Add((text, image));
                return image is not null ? Task.FromException(new IOException("secret-token")) : Task.CompletedTask;
            }
        }, "", CancellationToken.None);
        Assert.Equal(1, monitor.Captures);
        Assert.Equal(stage == "图片发送" ? 2 : 1, replies.Count);
        Assert.Null(replies[^1].Image);
        if (stage == "系统状态获取") Assert.Equal("无法获取系统状态，请稍后重试", replies[^1].Text);
        else Assert.Contains("CPU：12.3%", replies[^1].Text);
        var warning = Assert.Single(logger.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning));
        Assert.Contains($"{stage}失败", warning.Message);
        Assert.DoesNotContain("secret-", warning.Message);
        Assert.DoesNotContain("secret-", Assert.Single(logger.Diagnostics));
        Assert.DoesNotContain("secret-", replies[^1].Text);
    }

    [Fact]
    public async Task FailedTextFallbackDoesNotRetryOrLogSuccess()
    {
        var logger = new CommandBotTestObserver();
        using var processor = new CommandProcessor("Test", new() { TelegramBotToken = "secret-token" }, logger);
        var calls = 0;
        await processor.ProcessAsync(new("origin", "sender", "帮助")
        {
            Reply = (_, _, _) => { calls++; throw new IOException("secret-token"); }
        }, "", CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("图片发送失败"));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("处理或发送回复失败"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("命令已处理") || entry.Message.Contains("secret-token"));
        Assert.Equal(2, logger.Diagnostics.Count);
        Assert.DoesNotContain(logger.Diagnostics, details => details.Contains("secret-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScreenshotUsesExistingCaptureServiceAndRepliesWithOriginalImageOnly()
    {
        var bytes = JpegImage();
        var provider = new ScreenshotProvider(() => new(DateTimeOffset.UtcNow, bytes, "image/jpeg"));
        using var processor = new CommandProcessor("Test", new(), NullLogger.Instance, new(provider));
        var replies = new List<(string Text, byte[]? Image)>();
        await processor.ProcessAsync(new("origin", "sender", " 桌面截图 ")
        {
            Reply = (text, image, _) => { replies.Add((text, image)); return Task.CompletedTask; }
        }, "", CancellationToken.None);
        var reply = Assert.Single(replies);
        Assert.Equal("", reply.Text);
        Assert.Same(bytes, reply.Image);
        Assert.Equal(1, provider.Captures);
        Assert.Same(bytes, NotificationContent.ToJpegBytes(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureFailureAndEmptyImageReturnSafeTextWithoutExceptionDetails(bool empty)
    {
        var provider = new ScreenshotProvider(() => empty
            ? new(DateTimeOffset.UtcNow, [], "image/jpeg") : throw new DesktopCaptureException("secret-path"));
        using var processor = new CommandProcessor("Test", new(), NullLogger.Instance, new(provider));
        var replies = new List<(string Text, byte[]? Image)>();
        await processor.ProcessAsync(new("origin", "sender", "桌面截图")
        {
            Reply = (text, image, _) => { replies.Add((text, image)); return Task.CompletedTask; }
        }, "", CancellationToken.None);
        var reply = Assert.Single(replies);
        Assert.Null(reply.Image);
        Assert.Contains("无法获取桌面截图", reply.Text);
        Assert.DoesNotContain("secret-path", reply.Text);
        Assert.Equal(1, provider.Captures);
    }

    [Fact]
    public async Task ScreenshotSendFailureRepliesWithTextOnceWithoutRecapturing()
    {
        var provider = new ScreenshotProvider(() => new(DateTimeOffset.UtcNow, JpegImage(), "image/jpeg"));
        using var processor = new CommandProcessor("Test", new(), NullLogger.Instance, new(provider));
        var replies = new List<(string Text, byte[]? Image)>();
        await processor.ProcessAsync(new("origin", "sender", "桌面截图")
        {
            Reply = (text, image, _) =>
            {
                replies.Add((text, image));
                return image is null ? Task.CompletedTask : Task.FromException(new IOException("secret-token"));
            }
        }, "", CancellationToken.None);
        Assert.Equal(2, replies.Count);
        Assert.NotNull(replies[0].Image);
        Assert.Null(replies[1].Image);
        Assert.Contains("桌面截图发送失败", replies[1].Text);
        Assert.DoesNotContain("secret-token", replies[1].Text);
        Assert.Equal(1, provider.Captures);
    }

    [Fact]
    public async Task CancelledCommandDoesNotCaptureOrReply()
    {
        var provider = new ScreenshotProvider(() => throw new InvalidOperationException());
        using var processor = new CommandProcessor("Test", new(), NullLogger.Instance, new(provider));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(
            new("origin", "sender", "桌面截图") { Reply = (_, _, _) => throw new InvalidOperationException() },
            "", cancellation.Token));
        Assert.Equal(0, provider.Captures);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("group")]
    public void QQBotAndOneBotParsersRetainReplyContextButDoNotExposeItThroughJson(string type)
    {
        var onebot = OneBotCommandBotTransport.ParseMessage(JsonSerializer.SerializeToElement(new
        {
            post_type = "message", message_type = type, self_id = "bot", user_id = "user", group_id = "group", message_id = "source", message = "帮助"
        }))!.Value.Message;
        var qqbot = QQBotCommandBotTransport.ParseMessage(JsonSerializer.SerializeToElement(new
        {
            op = 0,
            t = type == "private" ? "C2C_MESSAGE_CREATE" : "GROUP_MESSAGE_CREATE",
            d = new { id = "source", author = new { user_openid = "user", member_openid = "user" }, group_openid = "group", content = "帮助" }
        }))!;
        foreach (var parsed in new[] { onebot, qqbot })
        {
            Assert.Equal(type, parsed.ConversationType);
            Assert.Equal("source", parsed.MessageId);
            Assert.Equal(type == "private" ? "user" : "group", parsed.ChatId);
            Assert.DoesNotContain("MessageId", JsonSerializer.Serialize(parsed));
            Assert.DoesNotContain("Reply", JsonSerializer.Serialize(parsed));
        }
    }

    [Fact]
    public async Task ImageFailureFallsBackOnceAndUnknownCommandsDoNotReply()
    {
        using var processor = new CommandProcessor("Test", new(), NullLogger.Instance);
        var calls = new List<(string Text, byte[]? Image)>();
        var message = new CommandBotMessage("origin", "sender", "帮助")
        {
            Reply = (text, image, _) =>
            {
                calls.Add((text, image));
                if (image is not null) throw new IOException();
                return Task.CompletedTask;
            }
        };
        await processor.ProcessAsync(message, "!", CancellationToken.None);
        Assert.Equal(2, calls.Count);
        Assert.NotNull(calls[0].Image);
        Assert.Null(calls[1].Image);
        Assert.Contains("!帮助", calls[1].Text);
        await processor.ProcessAsync(message with { Content = "未知" }, "!", CancellationToken.None);
        Assert.Equal(2, calls.Count);
    }

    [Theory]
    [InlineData("private", "users")]
    [InlineData("group", "groups")]
    public async Task QQBotReplyUsesOriginalTargetMessageAndSequenceNotNotificationSelection(string type, string segment)
    {
        var requests = new List<(Uri Uri, string Body)>();
        using var handler = new Handler(async request =>
        {
            requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync()));
            return request.RequestUri!.Host == "bots.qq.com" ? "{\"access_token\":\"access\",\"expires_in\":3600}" : "{}";
        });
        using var client = new HttpClient(handler);
        using var sender = new QQBotNotificationChannel(client);
        await sender.ReplyAsync(new() { QQBotAppId = "app", QQBotClientSecret = "secret", QQBotOpenId = "other-user", QQBotGroupOpenId = "other-group", QQBotSendTarget = "auto" },
            new("original", "sender", "帮助") { ConversationType = type, MessageId = "source-message" },
            "帮助", null, 2, CancellationToken.None);
        var sent = Assert.Single(requests.Where(item => item.Uri.Host == "api.sgroup.qq.com"));
        Assert.Equal($"/v2/{segment}/original/messages", sent.Uri.AbsolutePath);
        using var payload = JsonDocument.Parse(sent.Body);
        Assert.Equal("source-message", payload.RootElement.GetProperty("msg_id").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("msg_seq").GetInt32());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("markdown")]
    public async Task QQBotPictureReusesUploadAndSendsPassiveImageReply(string format)
    {
        string? body = null;
        using var handler = new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "bots.qq.com") return "{\"access_token\":\"access\",\"expires_in\":3600}";
            if (path.EndsWith("/upload_prepare")) return "{\"upload_id\":\"upload\",\"block_size\":\"1048576\",\"parts\":[]}";
            if (path.EndsWith("/files")) return "{\"file_info\":\"uploaded\"}";
            if (path.EndsWith("/messages")) body = await request.Content!.ReadAsStringAsync();
            return "{}";
        });
        using var client = new HttpClient(handler);
        using var sender = new QQBotNotificationChannel(client);
        await sender.ReplyAsync(new() { QQBotAppId = "app", QQBotClientSecret = "secret", QQBotMessageFormat = format },
            new("group", "sender", "帮助") { ConversationType = "group", MessageId = "source" },
            "", CommandImageRenderer.RenderHelp(CommandProcessor.Definitions, ""), 1, CancellationToken.None);
        using var payload = JsonDocument.Parse(body!);
        Assert.Equal(7, payload.RootElement.GetProperty("msg_type").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("markdown", out _));
        Assert.Equal("", payload.RootElement.GetProperty("content").GetString());
        Assert.Equal("source", payload.RootElement.GetProperty("msg_id").GetString());
        Assert.Equal("uploaded", payload.RootElement.GetProperty("media").GetProperty("file_info").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FeishuReplyUsesApplicationAndSourceMessageInsteadOfWebhook(bool picture, bool jpeg)
    {
        var requests = new List<(Uri Uri, string Body)>();
        using var handler = new Handler(async request =>
        {
            if (request.Content is MultipartFormDataContent multipart)
            {
                var imagePart = Assert.Single(multipart.Where(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "image"));
                Assert.Equal(jpeg ? "image/jpeg" : "image/png", imagePart.Headers.ContentType!.MediaType);
            }
            requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync()));
            return request.RequestUri!.AbsolutePath switch
            {
                "/open-apis/auth/v3/tenant_access_token/internal" => "{\"code\":0,\"tenant_access_token\":\"access\"}",
                "/open-apis/im/v1/images" => "{\"code\":0,\"data\":{\"image_key\":\"image\"}}",
                _ => "{\"code\":0}"
            };
        });
        using var client = new HttpClient(handler);
        using var sender = new FeishuNotificationChannel(client);
        await sender.ReplyAsync(new() { FeishuAppId = "app", FeishuAppSecret = "secret", FeishuWebhookUrl = "https://wrong.test/webhook" },
            new("origin-chat", "sender", "帮助") { MessageId = "source" }, "帮助",
            picture ? (jpeg ? JpegImage() : CommandImageRenderer.RenderHelp(CommandProcessor.Definitions, "")) : null, CancellationToken.None);
        Assert.DoesNotContain(requests, item => item.Uri.Host == "wrong.test");
        var sent = Assert.Single(requests.Where(item => item.Uri.AbsolutePath.EndsWith("/reply")));
        Assert.Equal("/open-apis/im/v1/messages/source/reply", sent.Uri.AbsolutePath);
        using var payload = JsonDocument.Parse(sent.Body);
        Assert.Equal(picture ? "image" : "text", payload.RootElement.GetProperty("msg_type").GetString());
    }

    [Fact]
    public async Task FeishuRejectsBusinessErrorEvenWhenHttpIsSuccessful()
    {
        using var handler = new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("auth/")
            ? "{\"tenant_access_token\":\"access\"}" : "{\"code\":99999}"));
        using var client = new HttpClient(handler);
        using var sender = new FeishuNotificationChannel(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.ReplyAsync(new(),
            new("origin", "sender", "帮助") { MessageId = "source" }, "帮助", null, CancellationToken.None));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/event", true)]
    [InlineData("/", false)]
    [InlineData("/event", false)]
    public async Task OneBotRepliesThroughWebSocketAndValidatesApiAcknowledgement(string path, bool success)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        var requestSeen = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Map("/{**path}", async context =>
        {
            Assert.Equal("Bearer own-token", context.Request.Headers.Authorization.ToString());
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                if (context.Request.Path == "/event")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                    return;
                }
                var data = new byte[65536];
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(data), context.RequestAborted);
                using var document = JsonDocument.Parse(data.AsMemory(0, result.Count));
                requestSeen.TrySetResult(document.RootElement.Clone());
                await acknowledge.Task.WaitAsync(context.RequestAborted);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { status = success ? "ok" : "failed", retcode = success ? 0 : 100, echo = document.RootElement.GetProperty("echo").GetString() }),
                    WebSocketMessageType.Text, true, context.RequestAborted);
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (Exception) when (context.RequestAborted.IsCancellationRequested || socket.State != WebSocketState.Open) { }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("http://", "ws://") + path;
        var ready = new TaskCompletionSource<Func<CommandBotMessage, string, byte[]?, CancellationToken, Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = OneBotCommandBotTransport.RunAsync(new(true, address, "own-token"), () => { }, _ => { }, lifetime.Token,
            sender => { if (sender is not null) ready.TrySetResult(sender); });
        try
        {
            var reply = await ready.Task.WaitAsync(lifetime.Token);
            var sending = reply(new("original-group", "sender", "帮助") { ConversationType = "group", MessageId = "source" }, "帮助", new byte[] { 1, 2 }, lifetime.Token);
            var payload = await requestSeen.Task.WaitAsync(lifetime.Token);
            Assert.False(sending.IsCompleted);
            acknowledge.TrySetResult();
            if (success) await sending;
            else await Assert.ThrowsAsync<InvalidOperationException>(() => sending);
            Assert.Equal("send_msg", payload.GetProperty("action").GetString());
            Assert.Equal("original-group", payload.GetProperty("params").GetProperty("group_id").GetString());
            Assert.Equal(2, payload.GetProperty("params").GetProperty("message").GetArrayLength());
        }
        finally
        {
            acknowledge.TrySetResult();
            lifetime.Cancel();
            try { await run; }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TelegramReplyUsesOriginalChatAndTopicAndCorrectImageFormat(bool jpeg)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapPost("/bottoken/sendPhoto", async context =>
        {
            var form = await context.Request.ReadFormAsync();
            Assert.Equal("original", form["chat_id"].ToString());
            Assert.Equal("12", form["message_thread_id"].ToString());
            Assert.Single(form.Files);
            Assert.Equal(jpeg ? "image.jpg" : "image.png", form.Files[0].FileName);
            Assert.Equal(jpeg ? "image/jpeg" : "image/png", form.Files[0].ContentType);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":true}");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await TelegramNotificationChannel.SendToAsync(new() { TelegramApiBaseUrl = address, TelegramBotToken = "token", TelegramChatId = "wrong" },
            "original", "", jpeg ? JpegImage() : CommandImageRenderer.RenderHelp(CommandProcessor.Definitions, ""), 12, CancellationToken.None);
    }

    [Fact]
    public async Task FeishuCachesTokenAcrossConcurrentRepliesAndInvalidatesChangedCredentials()
    {
        var requests = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("auth/"))
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult("{\"code\":0,\"tenant_access_token\":\"access\",\"expire\":7200}");
            }
            return Task.FromResult("{\"code\":0}");
        });
        using var client = new HttpClient(handler);
        using var sender = new FeishuNotificationChannel(client);
        var settings = new NotificationSettings { FeishuAppId = "app", FeishuAppSecret = "secret" };
        var message = new CommandBotMessage("origin", "sender", "帮助") { MessageId = "source" };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sender.ReplyAsync(settings, message, "回复", null, CancellationToken.None)));
        Assert.Equal(1, requests);
        await sender.ReplyAsync(settings with { FeishuAppSecret = "changed" }, message, "回复", null, CancellationToken.None);
        Assert.Equal(2, requests);
        await sender.ReplyAsync(settings with { FeishuAppId = "another" }, message, "回复", null, CancellationToken.None);
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("\"7200\"")]
    public async Task FeishuDoesNotCacheInvalidExpiry(string expiry)
    {
        var requests = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("auth/"))
            {
                requests++;
                return Task.FromResult($"{{\"tenant_access_token\":\"access\",\"expire\":{expiry}}}");
            }
            return Task.FromResult("{\"code\":0}");
        });
        using var client = new HttpClient(handler);
        using var sender = new FeishuNotificationChannel(client);
        var message = new CommandBotMessage("origin", "sender", "帮助") { MessageId = "source" };
        await sender.ReplyAsync(new(), message, "回复", null, CancellationToken.None);
        await sender.ReplyAsync(new(), message, "回复", null, CancellationToken.None);
        Assert.Equal(2, requests);
    }

    private static byte[] JpegImage()
    {
        using var bitmap = new Bitmap(16, 12);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Jpeg);
        return output.ToArray();
    }

    private sealed class ScreenshotProvider(Func<DesktopScreenshot> capture) : IDesktopScreenshotProvider
    {
        internal int Captures;
        public DesktopScreenshot Capture() { Captures++; return capture(); }
    }

    private sealed class SystemMonitor(Func<SystemResourceStatus> capture) : ISystemResourceMonitor
    {
        internal int Captures;
        public SystemResourceStatus Capture(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Captures++;
            return capture();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await respond(request)) };
    }
}
