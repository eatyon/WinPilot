using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Core;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.Features.Logs;
using WinPilot.Infrastructure;
using WinPilot.Features.Plugins;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class NotificationDiagnosticsTest
{
    [Fact]
    public void DiagnosticsRetainExceptionChainStackAndCodesWithoutCredentials()
    {
        var failure = Assert.Throws<HttpRequestException>(ThrowDiagnosticFailure);
        var text = NotificationFailureDiagnostics.Format(failure, ["plain-secret", "secret/value"]);
        Assert.Contains("System.Net.Http.HttpRequestException", text);
        Assert.Contains("System.ComponentModel.Win32Exception", text);
        Assert.Contains(nameof(ThrowDiagnosticFailure), text);
        Assert.Contains("HTTP: 403", text);
        Assert.Contains("NativeErrorCode: 5", text);
        Assert.Contains("HResult:", text);
        foreach (var value in new[] { "https://", "wss://", "plain-secret", "secret/value", "secret%2Fvalue", "header-value", "field-value" })
            Assert.DoesNotContain(value, text);
        Assert.Contains("[URL]", text);
        Assert.Contains("[REDACTED]", text);
    }

    private static void ThrowDiagnosticFailure() => throw new HttpRequestException(
        "https://user:password@example.test/bot/token?q=private plain-secret secret%2Fvalue Authorization=header-value",
        new Win32Exception(5, "wss://private.test/?token=private Bearer header-value client_secret=field-value"), HttpStatusCode.Forbidden);

    [Fact]
    public void AggregateDiagnosticsIncludeEveryInnerFailure()
    {
        var text = NotificationFailureDiagnostics.Format(new AggregateException(new IOException("first"), new JsonException("second")));
        Assert.Contains("System.IO.IOException: first", text);
        Assert.Contains("System.Text.Json.JsonException: second", text);
    }

    [Theory]
    [InlineData("Authorization: Bearer header-secret")]
    [InlineData("Authorization=Basic header-secret")]
    [InlineData("{\"token\":\"header-secret\"}")]
    [InlineData("password='header-secret'")]
    public void CredentialFieldsAreRedactedWithoutLosingExceptionType(string message)
    {
        var text = NotificationFailureDiagnostics.Format(new IOException(message));
        Assert.DoesNotContain("header-secret", text);
        Assert.Contains("System.IO.IOException", text);
    }

    [Fact]
    public void ConfiguredUrlCredentialsAreMaskedEvenOutsideTheUrl()
    {
        var settings = new NotificationSettings { WorkWeixinWebhookUrl = "https://example.test/?key=query-secret",
            TelegramProxyUrl = "http://proxy-user:proxy-secret@localhost:7890" };
        var text = NotificationFailureDiagnostics.Format(new IOException("query-secret proxy-secret"), NotificationFailureDiagnostics.Secrets(settings));
        Assert.DoesNotContain("query-secret", text);
        Assert.DoesNotContain("proxy-secret", text);
    }

    [Theory]
    [InlineData("https://user:password@private.test/token")]
    [InlineData("https:\\/\\/user:password@private.test/token")]
    public void UrlRedactionPrecedesCredentialReplacement(string url)
    {
        var text = NotificationFailureDiagnostics.Format(new IOException(url), ["https"]);
        Assert.Contains("[URL]", text);
        Assert.DoesNotContain("password", text);
        Assert.DoesNotContain("private.test", text);
    }

    [Fact]
    public async Task OneBotCommandCredentialsAreIndependentFromNotificationCredentials()
    {
        var logger = new CaptureLogger<PluginCommandRegistry>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, logger);
        using var scope = registry.CreateScope("plugin", "插件");
        scope.Register(new("查询", "说明"), (_, _) => throw new IOException("command-token websocket-password"));
        using var processor = new CommandProcessor("OneBot", new() { OneBotToken = "notification-token" }, logger, pluginCommands: registry,
            botSettings: new() { OneBotToken = "command-token", OneBotWebSocketUrl = "ws://user:websocket-password@localhost:6700" });
        await processor.ProcessAsync(new("origin", "sender", "查询"), "", CancellationToken.None);
        var entry = Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Warning));
        Assert.DoesNotContain("command-token", entry.Message);
        Assert.DoesNotContain("websocket-password", entry.Message);
    }

    [Fact]
    public async Task NotificationFailureLogsOnceAndKeepsFailureResultAndCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Notification.Diagnostics.Test", Guid.NewGuid().ToString("N"));
        var logger = new CaptureLogger<NotificationService>();
        try
        {
            using var service = new NotificationService(PortableDataPaths.Create(directory), logger);
            var result = await service.SendChannel("qqbot", () => throw new IOException("plain-secret", new Win32Exception(5)),
                CancellationToken.None, ["plain-secret"]);
            Assert.False(result.Succeeded);
            Assert.Equal("发送失败", result.Error);
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.NotNull(entry.Exception);
            Assert.Null(entry.Exception.InnerException);
            Assert.Contains("System.ComponentModel.Win32Exception", entry.Message);
            Assert.DoesNotContain("plain-secret", entry.Message);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendChannel("qqbot",
                () => Task.FromCanceled(cancellation.Token), cancellation.Token));
            Assert.Single(logger.Entries);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FileLogKeepsShortSummaryAndSafeExpandableDetails()
    {
        var paths = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Notification.Log.Test", Guid.NewGuid().ToString("N")));
        try
        {
            using var provider = new RollingFileLoggerProvider(paths.LogsDirectory);
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            using var service = new NotificationService(paths, factory.CreateLogger<NotificationService>());
            var cause = new IOException("plain-secret https://private.test/", new Win32Exception(5, "native cause"));
            await service.SendChannel("qqbot", () => throw cause, CancellationToken.None, ["plain-secret"]);
            var logs = new LogQueryService(paths);
            var date = Assert.Single(logs.GetAvailableDates()).Value;
            var entry = Assert.Single(logs.Query(new(date, null, null, 0, 10)).Entries);
            Assert.Equal("通知渠道 QQBot 发送失败：IOException", entry.Message);
            Assert.Contains("System.IO.IOException", entry.Details!);
            Assert.Contains("System.ComponentModel.Win32Exception", entry.Details!);
            Assert.DoesNotContain("plain-secret", entry.Details!);
            Assert.DoesNotContain("private.test", entry.Details!);
            var snapshot = NotificationFailureDiagnostics.Redact(cause, ["plain-secret"]);
            Assert.NotSame(cause, snapshot);
            Assert.Null(snapshot.InnerException);
        }
        finally { if (Directory.Exists(paths.ApplicationDirectory)) Directory.Delete(paths.ApplicationDirectory, true); }
    }

    [Theory]
    [InlineData("查询")]
    [InlineData("*")]
    public async Task PluginCommandDiagnosticsAreLoggedOnlyAtRegistryBoundary(string name)
    {
        var registryLogger = new CaptureLogger<PluginCommandRegistry>();
        var processorLogger = new CaptureLogger<CommandProcessor>();
        var registry = new PluginCommandRegistry(CommandProcessor.IsSupportedCommand, registryLogger);
        using var scope = registry.CreateScope("plugin", "插件");
        scope.Register(new(name, "说明"), (_, _) => throw new IOException("plain-secret", new JsonException("inner failure")));
        using var processor = new CommandProcessor("Test", new() { QQBotClientSecret = "plain-secret" }, processorLogger, pluginCommands: registry);
        await processor.ProcessAsync(new("origin", "sender", "查询"), "", CancellationToken.None);
        var entry = Assert.Single(registryLogger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.NotNull(entry.Exception);
        Assert.Null(entry.Exception.InnerException);
        Assert.Contains("System.Text.Json.JsonException: inner failure", entry.Message);
        Assert.DoesNotContain("plain-secret", entry.Message);
        Assert.DoesNotContain(processorLogger.Entries, item => item.Message.Contains("System.IO.IOException", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("qqbot")]
    [InlineData("onebot")]
    [InlineData("feishu")]
    public async Task ReconnectDiagnosticsAreLimitedAndCancellationIsNotLogged(string channel)
    {
        var logger = new CaptureLogger<object>();
        var state = new CommandBotStatus(true, "connecting");
        using var cancellation = new CancellationTokenSource();
        var attemptedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        Task Fail()
        {
            if (++attempts == 2) attemptedAgain.TrySetResult();
            throw new IOException("plain-secret https://private.test/", new WebSocketException("connection failure"));
        }
        CommandBotReceiver receiver = channel switch
        {
            "qqbot" => new QQBotCommandBotReceiver(new LoggerAdapter<QQBotCommandBotService>(logger), (_, _, _, _, _) => Fail(), _ => { }, () => state, value => state = value, _ => { }),
            "onebot" => new OneBotCommandBotReceiver(new LoggerAdapter<OneBotCommandBotService>(logger), (_, _, _, _) => Fail(), () => state, value => state = value, _ => { }),
            _ => new FeishuCommandBotReceiver(new LoggerAdapter<FeishuCommandBotService>(logger), (_, _, _, _, _, _) => Fail(), () => state, value => state = value, _ => { })
        };
        var running = receiver.RunAsync(new() { Notifications = new() { QQBotClientSecret = "plain-secret", FeishuAppSecret = "plain-secret" },
            CommandBots = new() { OneBotToken = "plain-secret" } }, cancellation.Token);
        try { await attemptedAgain.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.NotNull(entry.Exception);
        Assert.Null(entry.Exception.InnerException);
        Assert.Contains("System.Net.WebSockets.WebSocketException", entry.Message);
        Assert.DoesNotContain("plain-secret", entry.Message);
        Assert.DoesNotContain("private.test", entry.Message);
        Assert.Equal("reconnecting", state.State);
    }

    [Theory]
    [InlineData("qqbot")]
    [InlineData("onebot")]
    [InlineData("feishu")]
    public async Task PermanentConnectionFailureKeepsStateAndRecordsDiagnostic(string channel)
    {
        var logger = new CaptureLogger<object>();
        var state = new CommandBotStatus(true, "connecting");
        CommandBotReceiver receiver = channel switch
        {
            "qqbot" => new QQBotCommandBotReceiver(new LoggerAdapter<QQBotCommandBotService>(logger), (_, _, _, _, _) => throw new QQBotConnectionException("永久失败", true), _ => { }, () => state, value => state = value, _ => { }),
            "onebot" => new OneBotCommandBotReceiver(new LoggerAdapter<OneBotCommandBotService>(logger), (_, _, _, _) => throw new OneBotConnectionException("永久失败", true), () => state, value => state = value, _ => { }),
            _ => new FeishuCommandBotReceiver(new LoggerAdapter<FeishuCommandBotService>(logger), (_, _, _, _, _, _) => throw new FeishuConnectionException("永久失败", true), () => state, value => state = value, _ => { })
        };
        await receiver.RunAsync(new(), CancellationToken.None);
        Assert.Equal("failed", state.State);
        Assert.Equal("永久失败", state.Error);
        Assert.Contains("ConnectionException", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public async Task ExhaustedReconnectRecordsTerminalCause()
    {
        var logger = new CaptureLogger<FeishuCommandBotService>();
        var state = new CommandBotStatus(true, "connecting");
        var receiver = new FeishuCommandBotReceiver(logger, (_, _, _, _, configure, _) =>
        {
            configure(new() { ReconnectCount = 0 });
            throw new IOException("terminal cause");
        }, () => state, value => state = value, _ => { });
        await receiver.RunAsync(new(), CancellationToken.None);
        Assert.Equal("failed", state.State);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("次数已达上限", entry.Message);
        Assert.Contains("System.IO.IOException: terminal cause", entry.Message);
    }

    [Theory]
    [InlineData("timeout", "获取超时，请重试", false)]
    [InlineData("http-timeout", "QQBot 连接失败，请检查网络和平台配置后重试", true)]
    [InlineData("network", "QQBot 连接失败，请检查网络和平台配置后重试", true)]
    [InlineData("gateway", "QQBot 连接失败，请检查网络和平台配置后重试", true)]
    [InlineData("gateway-close", "QQBot 连接失败，请检查网络和平台配置后重试", true)]
    [InlineData("configuration", "QQBot AppID 或 AppSecret 未配置", false)]
    [InlineData("invalid-operation", "获取 OpenID 失败，请检查程序日志", true)]
    [InlineData("protocol", "获取 OpenID 失败，请检查程序日志", true)]
    [InlineData("unexpected-cancel", "获取 OpenID 失败，请检查程序日志", true)]
    public async Task BindingSseClassifiesFailureWithoutExposingMessages(string kind, string expected, bool logged)
    {
        await using var fixture = new BindingFixture(kind);
        await fixture.App.StartAsync();
        using var client = fixture.App.GetTestClient();
        using var response = await client.PostAsync("/api/notifications/qqbot/bind-user", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache);
        var body = await response.Content.ReadAsStringAsync();
        using var data = JsonDocument.Parse(body[6..].Trim());
        Assert.Equal("error", data.RootElement.GetProperty("type").GetString());
        Assert.Equal(expected, data.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("plain-secret", body);
        var entries = fixture.Logger.Entries.Where(item => item.Message.Contains("QQBot 获取 OpenID 失败", StringComparison.Ordinal)).ToArray();
        Assert.Equal(logged ? 1 : 0, entries.Length);
        foreach (var entry in entries)
        {
            Assert.NotNull(entry.Exception);
            Assert.Null(entry.Exception.InnerException);
            Assert.DoesNotContain("plain-secret", entry.Message);
            Assert.DoesNotContain("private.test", entry.Message);
        }
    }

    [Fact]
    public async Task BindingRequestCancellationPropagatesWithoutErrorEvent()
    {
        await using var fixture = new BindingFixture("cancel");
        var propagated = false;
        fixture.App.Use(async (context, next) =>
        {
            context.RequestAborted = new CancellationToken(true);
            try { await next(context); }
            catch (OperationCanceledException) { propagated = true; }
        });
        await fixture.App.StartAsync();
        using var client = fixture.App.GetTestClient();
        using var response = await client.PostAsync("/api/notifications/qqbot/bind-user", null);
        Assert.True(propagated);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Empty(fixture.Logger.Entries.Where(item => item.Level >= LogLevel.Warning));
    }

    [Fact]
    public async Task ApplicationStoppingCancelsBindingThroughRequestMiddleware()
    {
        await using var fixture = new BindingFixture("cancel");
        fixture.App.UseMiddleware<RequestCancellationMiddleware>();
        await fixture.App.StartAsync();
        fixture.App.Lifetime.StopApplication();
        using var client = fixture.App.GetTestClient();
        using var response = await client.PostAsync("/api/notifications/qqbot/bind-user", null);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Message.Contains("QQBot 获取 OpenID 失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BindingTimeoutOwnershipDoesNotMisclassifyCancellation()
    {
        var timedOut = await Assert.ThrowsAsync<QQBotBindingTimeoutException>(() => QQBotBindingService.WithTimeoutAsync(
            async token => { await Task.Delay(Timeout.Infinite, token); return "never"; }, CancellationToken.None, TimeSpan.Zero));
        Assert.IsAssignableFrom<OperationCanceledException>(timedOut.InnerException);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QQBotBindingService.WithTimeoutAsync(
            token => Task.FromCanceled<string>(token), cancellation.Token, TimeSpan.Zero));
        var unrelated = new OperationCanceledException("not a binding timeout");
        Assert.Same(unrelated, await Assert.ThrowsAsync<OperationCanceledException>(() => QQBotBindingService.WithTimeoutAsync(
            _ => throw unrelated, CancellationToken.None, Timeout.InfiniteTimeSpan)));
    }

    [Fact]
    public async Task HttpTimeoutIsClassifiedAtHttpBoundaryAndCallerCancellationStillPropagates()
    {
        var timeout = new OperationCanceledException("HTTP cancellation", new TimeoutException());
        using var client = new HttpClient(new FailingHttpHandler(timeout));
        var connection = await Assert.ThrowsAsync<HttpRequestException>(() => QQBotBindingService.GetConnectionAsync(client, "app", "secret", CancellationToken.None));
        Assert.Same(timeout, connection.InnerException);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QQBotBindingService.GetConnectionAsync(client, "app", "secret", cancellation.Token));
        var unknown = new OperationCanceledException("unrelated");
        using var otherClient = new HttpClient(new FailingHttpHandler(unknown));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QQBotBindingService.GetConnectionAsync(otherClient, "app", "secret", CancellationToken.None));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("cleanup")]
    [InlineData("cancel")]
    public async Task CommandBotSaveFailurePreservesExceptionAndCancellation(string kind)
    {
        await using var fixture = new BindingFixture("unused");
        var context = new DefaultHttpContext { RequestServices = fixture.App.Services };
        var safeMode = fixture.App.Services.GetRequiredService<SafeModeService>();
        Exception cause = kind switch
        {
            "permission" => new UnauthorizedAccessException("save failure"),
            "cleanup" => new AggregateException(new IOException("save failure"), new IOException("cleanup failure")),
            _ => new OperationCanceledException()
        };
        var operation = CommandBotEndpointRouteBuilderExtensions.ExecuteAsync(() => Task.FromException(cause), fixture.Bot, safeMode, context, "qqbot", "保存设置");
        if (kind == "cancel")
        {
            Assert.Same(cause, await Assert.ThrowsAsync<OperationCanceledException>(() => operation));
            Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Message.Contains("命令机器人配置操作失败", StringComparison.Ordinal));
        }
        else
        {
            var result = Assert.IsAssignableFrom<IStatusCodeHttpResult>(await operation);
            Assert.Equal(500, result.StatusCode);
            var entry = Assert.Single(fixture.Logger.Entries.Where(entry => entry.Message.Contains("命令机器人配置操作失败", StringComparison.Ordinal)));
            Assert.Same(cause, entry.Exception);
            Assert.Equal(LogLevel.Error, entry.Level);
        }
    }

    [Fact]
    public async Task CommandBotSaveFailureHasOneDiagnosticWithoutConfigurationValues()
    {
        await using var fixture = new BindingFixture("unused");
        fixture.App.MapCommandBotEndpoints();
        await fixture.App.StartAsync();
        fixture.App.Services.GetRequiredService<SafeModeService>();
        using var client = fixture.App.GetTestClient();
        using var locked = new FileStream(fixture.Paths.ConfigurationFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var response = await client.PutAsJsonAsync("/api/settings/notifications/command-bots/qqbot", new { enabled = false, prefix = "private-prefix" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var entry = Assert.Single(fixture.Logger.Entries.Where(item => item.Message.Contains("命令机器人配置操作失败", StringComparison.Ordinal)));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsAssignableFrom<IOException>(entry.Exception);
        Assert.Contains("qqbot", entry.Message);
        Assert.Contains("保存设置", entry.Message);
        Assert.DoesNotContain("private-prefix", entry.Message);
        Assert.DoesNotContain("plain-secret", entry.Message);
    }

    private sealed class BindingFixture : IAsyncDisposable
    {
        internal PortableDataPaths Paths { get; } = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "WinPilot.Binding.Diagnostics.Test", Guid.NewGuid().ToString("N")));
        internal CaptureLogger<object> Logger { get; } = new();
        internal WebApplication App { get; }
        private readonly CommandBotConfiguration configuration = new();
        private readonly QQBotCommandBotService bot;
        internal QQBotCommandBotService Bot => bot;
        internal BindingFixture(string kind)
        {
            new WinPilotSettings { Notifications = new() { QQBotAppId = "app", QQBotClientSecret = "plain-secret" } }.Save(Paths.ConfigurationFile);
            bot = new(Paths, new LoggerAdapter<QQBotCommandBotService>(Logger), configuration, (_, _, _, _, _) => Task.CompletedTask,
                (_, _, _, token) => kind switch
                {
                    "timeout" => QQBotBindingService.WithTimeoutAsync(async timeout => { await Task.Delay(Timeout.Infinite, timeout); return "never"; }, token, TimeSpan.Zero),
                    "http-timeout" => throw new HttpRequestException("plain-secret", new TimeoutException("HTTP timeout")),
                    "network" => throw new HttpRequestException("https://private.test/ plain-secret"),
                    "gateway" => throw new WebSocketException("plain-secret"),
                    "gateway-close" => throw new QQBotGatewayClosedException(4004),
                    "configuration" => QQBotBindingService.BindAsync("", "", false, (_, _) => Task.CompletedTask, token),
                    "invalid-operation" => throw new InvalidOperationException("plain-secret"),
                    "protocol" => throw new JsonException("plain-secret"),
                    "unexpected-cancel" => throw new OperationCanceledException("plain-secret"),
                    "cancel" => Task.FromCanceled<string>(token),
                    _ => Task.FromResult("success")
                });
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new LoggerProvider(Logger));
            builder.Services.AddSingleton(Paths);
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(bot);
            builder.Services.AddSingleton<TerminalSessionManager>();
            builder.Services.AddSingleton<SafeModeService>();
            App = builder.Build();
            App.MapNotificationEndpoints(Paths.ConfigurationFile, new EmptyNotifications());
        }
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            bot.Dispose();
            configuration.Dispose();
            var directory = Paths.ApplicationDirectory;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class EmptyNotifications : INotificationService
    {
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default, string? channel = null, bool ignoreChannelEnabled = false)
            => Task.FromResult<IReadOnlyList<NotificationChannelResult>>([]);
    }
    private sealed class FailingHttpHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
    private sealed class CaptureLogger<T> : ILogger<T>
    {
        internal ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((level, formatter(state, exception) + (exception is null ? "" : "\n" + exception), exception));
    }
    private sealed class LoggerAdapter<T>(ILogger logger) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);
        public bool IsEnabled(LogLevel level) => logger.IsEnabled(level);
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => logger.Log(level, eventId, state, exception, formatter);
    }
    private sealed class LoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}
