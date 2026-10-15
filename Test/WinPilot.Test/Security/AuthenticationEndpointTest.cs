using WinPilot.Core;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Features.Security;
using WinPilot.Features.Notifications;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Plugins;
using WinPilot.Features.Terminal;
using WinPilot.Features.Updates;
using WinPilot.Infrastructure;
using WinPilot.Core.Notifications;
using WinPilot.Web;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class AuthenticationEndpointTest : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinPilot.Auth.Endpoint.Test", Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private HttpClient client = null!;
    private RollingFileLoggerProvider? logProvider;

    [Fact]
    public async Task PasswordChangeRejectsSessionFromPendingOldPasswordLogin()
    {
        using var handshake = new PendingLoginLogger();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(handshake);
        var paths = PortableDataPaths.Create(Path.Combine(root, "pending-login"));
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddSingleton<INotificationService, TestNotificationService>();
        builder.Services.AddSingleton<SecurityEventNotifier>();
        builder.Services.AddSingleton<TerminalSessionManager>();
        builder.Services.AddSingleton<SafeModeService>();
        builder.Services.AddSingleton<WebFacadeService>();
        await using var host = builder.Build();
        host.UseMiddleware<AuthenticationMiddleware>();
        host.MapAuthenticationEndpoints();
        host.MapGet("/api/protected", () => Results.Ok("protected"));
        var auth = host.Services.GetRequiredService<AuthStore>();
        var sessions = host.Services.GetRequiredService<SessionManager>();
        auth.SetInitialPassword("old-password");
        var existing = sessions.Create();
        await host.StartAsync();
        using var http = host.GetTestClient();
        var login = http.PostAsJsonAsync("/login", new { password = "old-password" });
        try
        {
            await handshake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var change = new HttpRequestMessage(HttpMethod.Put, "/api/settings/security/password")
            {
                Content = JsonContent.Create(new { currentPassword = "old-password", newPassword = "new-password", confirmation = "new-password" })
            };
            change.Headers.Add("Cookie", "WinPilot.Session=" + existing);
            using var changed = await http.SendAsync(change);
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
            Assert.False(sessions.Validate(existing));
            handshake.Release.Set();
            using var response = await login.WaitAsync(TimeSpan.FromSeconds(5));
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                Assert.False(response.Headers.Contains("Set-Cookie"));
            else
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
                Assert.False(sessions.Validate(cookie.Split('=', 2)[1]));
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
                request.Headers.Add("Cookie", cookie);
                using var protectedResponse = await http.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
            }
            Assert.False(auth.Verify("old-password"));
            Assert.True(auth.Verify("new-password"));
        }
        finally
        {
            handshake.Release.Set();
            using var pending = await login.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync();
        }
    }

    private sealed class PendingLoginLogger : ILoggerProvider, ILogger
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).Contains("安全事件：登录成功", StringComparison.Ordinal)) return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("登录握手未解除");
        }
        public void Dispose() => Release.Dispose();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Salt\":null,\"Hash\":null}")]
    [InlineData("{\"Hash\":\"AAAA\"}")]
    [InlineData("{\"Salt\":\"AAAA\"}")]
    public async Task StructurallyInvalidAuthRecordActivatesProtectionInsteadOfUnhandledError(string content)
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var store = app.Services.GetRequiredService<AuthStore>();
        var sessions = app.Services.GetRequiredService<SessionManager>();
        store.SetInitialPassword("a secure passphrase");
        var session = sessions.Create();
        using var warmup = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, warmup.StatusCode);
        File.WriteAllText(paths.AuthenticationFile, content);
        using var response = await client.GetAsync("/api/programs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(app.Services.GetRequiredService<SafeModeService>().Enabled);
        Assert.False(store.IsConfigured);
        Assert.False(sessions.Validate(session));
        Assert.Equal(content, File.ReadAllText(paths.AuthenticationFile));
    }

    [Theory]
    [InlineData(false, "access")]
    [InlineData(true, "access")]
    [InlineData(false, "operation")]
    [InlineData(true, "operation")]
    public async Task AclPersistenceFailuresReturnServerProblemAndLogOriginalException(bool existing, string kind)
    {
        var paths = PortableDataPaths.Create(Path.Combine(root, "acl-failure"));
        const string oldPassword = "old-private-passphrase";
        const string newPassword = "new-private-passphrase";
        if (existing) new AuthStore(paths).SetInitialPassword(oldPassword);
        var original = existing ? File.ReadAllBytes(paths.AuthenticationFile) : null;
        Exception cause = kind == "access" ? new UnauthorizedAccessException("ACL 原始权限异常") : new InvalidOperationException("ACL 原始系统异常");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        using var logs = new RollingFileLoggerProvider(paths.LogsDirectory);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(new AuthStore(paths, _ => throw cause));
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddSingleton<INotificationService, TestNotificationService>();
        builder.Services.AddSingleton<SecurityEventNotifier>();
        builder.Services.AddSingleton<WebFacadeService>();
        await using var faultApp = builder.Build();
        faultApp.MapAuthenticationEndpoints();
        await faultApp.StartAsync();
        using var faultClient = faultApp.GetTestClient();
        using var response = await faultClient.PutAsJsonAsync("/api/settings/security/password", new
        {
            currentPassword = oldPassword, newPassword, confirmation = newPassword
        });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("密码保存失败", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("无法保存密码，请检查数据目录权限", problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(root, body);
        Assert.DoesNotContain(cause.Message, body);
        Assert.DoesNotContain(oldPassword, body);
        Assert.DoesNotContain(newPassword, body);
        var log = ReadActiveLog(paths.LogsDirectory);
        Assert.Contains("密码保存失败", log);
        Assert.Contains(cause.GetType().Name, log);
        Assert.Contains(cause.Message, log);
        Assert.DoesNotContain(oldPassword, log);
        Assert.DoesNotContain(newPassword, log);
        Assert.False(File.Exists(paths.AuthenticationFile + ".tmp"));
        if (existing) Assert.Equal(original, File.ReadAllBytes(paths.AuthenticationFile));
        else Assert.False(File.Exists(paths.AuthenticationFile));
        using var invalid = await faultClient.PutAsJsonAsync("/api/settings/security/password", new
        {
            currentPassword = oldPassword, newPassword = "short", confirmation = "short"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("password", false)]
    [InlineData("password", true)]
    [InlineData("notifications", false)]
    [InlineData("notifications", true)]
    public async Task SecuritySettingPersistenceFailuresReturnFixedProblemWithoutSecrets(string setting, bool unauthorized)
    {
        var paths = PortableDataPaths.Create(Path.Combine(root, "persistence-failure"));
        WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        using var logs = new RollingFileLoggerProvider(paths.LogsDirectory);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddSingleton<INotificationService, TestNotificationService>();
        builder.Services.AddSingleton<SecurityEventNotifier>();
        builder.Services.AddSingleton<WebFacadeService>();
        await using var faultApp = builder.Build();
        faultApp.MapAuthenticationEndpoints();
        await faultApp.StartAsync();
        var blockedPath = setting == "password" ? paths.AuthenticationFile + ".tmp" : paths.ConfigurationFile;
        FileStream? lockedFile = null;
        try
        {
            if (unauthorized)
            {
                if (File.Exists(blockedPath)) File.Delete(blockedPath);
                Directory.CreateDirectory(blockedPath);
            }
            else lockedFile = new FileStream(blockedPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var faultClient = faultApp.GetTestClient();
            const string password = "test-password-not-for-logs";
            using HttpContent content = setting == "password"
                ? JsonContent.Create(new { currentPassword = "old-private-password", newPassword = password, confirmation = password })
                : JsonContent.Create(new SecurityNotificationSettings());
            using var response = await faultClient.PutAsync($"/api/settings/security/{setting}", content);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            using var json = System.Text.Json.JsonDocument.Parse(body);
            var title = setting == "password" ? "密码保存失败" : "安全通知设置保存失败";
            var detail = setting == "password" ? "无法保存密码，请检查数据目录权限" : "无法保存安全通知设置，请检查数据目录权限";
            Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(title, json.RootElement.GetProperty("title").GetString());
            Assert.Equal(detail, json.RootElement.GetProperty("detail").GetString());
            Assert.DoesNotContain(root, body);
            Assert.DoesNotContain(password, body);
            Assert.DoesNotContain("old-private-password", body);
            var log = ReadActiveLog(paths.LogsDirectory);
            Assert.Contains(title, log);
            Assert.Contains(unauthorized ? "UnauthorizedAccessException" : "IOException", log);
            Assert.DoesNotContain(password, log);
            Assert.DoesNotContain("old-private-password", log);
        }
        finally
        {
            lockedFile?.Dispose();
            if (Directory.Exists(blockedPath)) Directory.Delete(blockedPath);
        }
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddResponseCompression(options =>
            options.MimeTypes = ["text/html", "text/plain", "text/css", "text/javascript", "application/javascript", "application/json", "application/problem+json", "image/svg+xml"]);
        var paths = PortableDataPaths.Create(root);
        logProvider = new RollingFileLoggerProvider(paths.LogsDirectory);
        builder.Logging.AddProvider(logProvider);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddSingleton<INotificationService, TestNotificationService>();
        builder.Services.AddSingleton<CommandBotConfiguration>();
        builder.Services.AddSingleton<SecurityEventNotifier>();
        builder.Services.AddSingleton<WebFacadeService>();
        builder.Services.AddSingleton<TerminalSessionManager>();
        builder.Services.AddSingleton<PluginPageSessionManager>();
        builder.Services.AddSingleton<SafeModeService>();
        builder.Services.AddSingleton<UpdateService>();
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Remote-IP"].FirstOrDefault() ?? "192.168.1.10");
            await next();
        });
        app.UseResponseCompression();
        app.UseRouting();
        app.UseMiddleware<AuthenticationMiddleware>();
        app.UseMiddleware<SafeModeMiddleware>();
        app.UseWebSockets();
        app.MapHealthEndpoint();
        app.MapUpdateEndpoints();
        app.MapAuthenticationEndpoints();
        app.MapSafeModeEndpoints();
        app.MapSettingsEndpoints(paths.ConfigurationFile, new StartupTaskService());
        app.MapNotificationEndpoints(paths.ConfigurationFile, app.Services.GetRequiredService<INotificationService>());
        app.MapTerminalEndpoints();
        app.MapGet("/api/protected", () => Results.Ok(new { ok = true }));
        app.MapMethods("/api/protected/{name}", ["GET", "PUT", "DELETE"], () => Results.NoContent());
        foreach (var route in new[]
        {
            "/api/desktop/keyboard", "/api/programs", "/api/programs/users", "/api/programs/usage",
            "/api/programs/close", "/api/programs/launch", "/api/programs/tags", "/api/programs/tags/{name}",
            "/api/programs/tags/{name}/launch", "/api/tasks", "/api/tasks/{name}", "/api/tasks/{name}/run",
            "/api/tasks/{name}/enabled", "/api/plugins/{id}", "/api/plugins/{id}/readme",
            "/api/plugins/{id}/configuration", "/api/plugins/{id}/settings/display/status",
            "/api/plugins/{id}/settings/display/features", "/api/plugins/{id}/settings/display/configuration",
            "/api/plugins/{id}/enable", "/api/plugins/scan", "/api/plugins/options/settings/{source}",
            "/api/plugin-page-sessions/{sessionId}", "/api/plugin-page-sessions/{sessionId}/messages",
            "/api/settings/test",
            "/api/notifications/diagnostic",
        })
            app.MapMethods(route, ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"], () => Results.NoContent());
        app.MapGet("/assets/diagnostic.js", () => Results.Text(new string('a', 4096), "text/javascript"));
        Directory.CreateDirectory(root);
        var pluginAssetPath = Path.Combine(root, "diagnostic.js");
        File.WriteAllText(pluginAssetPath, new string('b', 4096));
        app.MapGet("/plugin-assets/diagnostic/page.js", () => Results.File(pluginAssetPath, "text/javascript"));
        app.MapGet("/api/system/status", () => Results.Ok(new { ok = true }));
        app.MapGet("/api/test-events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: connected\n\n", context.RequestAborted);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        app.MapGet("/", () => Results.Text("home"));
        app.MapGet("/login", () => Results.Text("login"));
        app.UseWebFallback();
        await app.StartAsync();
        client = app.GetTestClient();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("*/*", false)]
    [InlineData("application/json", false)]
    [InlineData("text/event-stream", false)]
    [InlineData("text/html", false)]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", false)]
    [InlineData(null, true)]
    [InlineData("*/*", true)]
    [InlineData("application/json", true)]
    [InlineData("text/event-stream", true)]
    [InlineData("text/html", true)]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", true)]
    public async Task UnauthenticatedApiReturnsEmpty401RegardlessOfAccept(string? accept, bool facade)
    {
        ConfigureAuthentication(facade);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        using var response = await client.SendAsync(request);
        await AssertApiUnauthorized(response);
    }

    [Theory]
    [InlineData("/api", false)]
    [InlineData("/api/", false)]
    [InlineData("/API/PROTECTED/", false)]
    [InlineData("/api/protected/task.json", false)]
    [InlineData("/api/unknown", false)]
    [InlineData("/api", true)]
    [InlineData("/api/", true)]
    [InlineData("/API/PROTECTED/", true)]
    [InlineData("/api/protected/task.json", true)]
    [InlineData("/api/unknown", true)]
    public async Task UnauthenticatedHtmlApiPathVariantsReturn401(string path, bool facade)
    {
        ConfigureAuthentication(facade);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request);
        await AssertApiUnauthorized(response);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    [InlineData("OPTIONS", false)]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("DELETE", true)]
    [InlineData("OPTIONS", true)]
    public async Task UnauthenticatedHtmlApiReturns401BeforeMethodMatching(string method, bool facade)
    {
        ConfigureAuthentication(facade);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/protected");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request);
        await AssertApiUnauthorized(response);
    }

    [Theory]
    [InlineData("/api/files/download", false)]
    [InlineData("/api/test-events", false)]
    [InlineData("/api/terminal/ws", false)]
    [InlineData("/api/files/download", true)]
    [InlineData("/api/test-events", true)]
    [InlineData("/api/terminal/ws", true)]
    public async Task UnauthenticatedHtmlDownloadAndLongConnectionsReturn401WithoutRedirect(string path, bool facade)
    {
        ConfigureAuthentication(facade);
        using var request = new HttpRequestMessage(path == "/api/files/download" ? HttpMethod.Post : HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html");
        if (path == "/api/files/download") request.Content = JsonContent.Create(new { paths = Array.Empty<string>() });
        if (path == "/api/terminal/ws")
        {
            request.Headers.Add("Connection", "Upgrade");
            request.Headers.Add("Upgrade", "websocket");
            request.Headers.Add("Sec-WebSocket-Version", "13");
            request.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await AssertApiUnauthorized(response);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.False(response.Headers.Contains("Upgrade"));
        Assert.False(response.Headers.Contains("Sec-WebSocket-Accept"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidOrRevokedSessionCannotRedirectHtmlApiToPage(bool revoked, bool facade)
    {
        ConfigureAuthentication(facade);
        var sessions = app.Services.GetRequiredService<SessionManager>();
        var session = revoked ? sessions.Create() : "unknown-session";
        if (revoked) sessions.RevokeAll();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        request.Headers.Add("Cookie", $"WinPilot.Session={session}");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request);
        await AssertApiUnauthorized(response);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedHtmlApiKeepsSuccessNotFoundAndMethodNotAllowed(bool facade)
    {
        ConfigureAuthentication(facade);
        client.DefaultRequestHeaders.Add("Cookie", $"WinPilot.Session={app.Services.GetRequiredService<SessionManager>().Create()}");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var success = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Null(success.Headers.Location);
        Assert.Equal("application/json", success.Content.Headers.ContentType?.MediaType);
        using var json = System.Text.Json.JsonDocument.Parse(await success.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        using var missing = await client.GetAsync("/api/unknown");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Null(missing.Headers.Location);
        Assert.Equal("API 不存在", (await missing.Content.ReadFromJsonAsync<ProblemDetails>())?.Title);
        using var wrongMethod = await client.PostAsync("/api/protected", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal(new[] { "GET" }, wrongMethod.Content.Headers.Allow);
        Assert.Null(wrongMethod.Headers.Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HtmlApiAndPageRemainAccessibleWhenPasswordIsNotConfigured(bool facade)
    {
        ConfigureAuthentication(facade, configured: false);
        Assert.False(app.Services.GetRequiredService<AuthStore>().IsConfigured);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        using var api = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Null(api.Headers.Location);
        using var json = System.Text.Json.JsonDocument.Parse(await api.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        using var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("home", await page.Content.ReadAsStringAsync());
        Assert.Null(page.Headers.Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedHtmlApiStillReachesSafeMode403WhileUnauthenticatedApiReturns401(bool facade)
    {
        ConfigureAuthentication(facade);
        app.Services.GetRequiredService<SafeModeService>().SetEnabled(true);
        using var authenticated = new HttpRequestMessage(HttpMethod.Get, "/api/programs");
        authenticated.Headers.Accept.ParseAdd("text/html");
        authenticated.Headers.Add("Cookie", $"WinPilot.Session={app.Services.GetRequiredService<SessionManager>().Create()}");
        using var forbidden = await client.SendAsync(authenticated);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Null(forbidden.Headers.Location);
        using var json = System.Text.Json.JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync());
        Assert.Equal("safe_mode_restricted", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("安全模式下不可执行此操作", json.RootElement.GetProperty("message").GetString());
        using var unauthenticated = new HttpRequestMessage(HttpMethod.Get, "/api/programs");
        unauthenticated.Headers.Accept.ParseAdd("text/html");
        using var unauthorized = await client.SendAsync(unauthenticated);
        await AssertApiUnauthorized(unauthorized);
    }

    [Theory]
    [InlineData("/", false)]
    [InlineData("/settings", false)]
    [InlineData("/apix", false)]
    [InlineData("/api-extra", false)]
    [InlineData("/", true)]
    [InlineData("/settings", true)]
    [InlineData("/apix", true)]
    [InlineData("/api-extra", true)]
    public async Task NonApiHtmlPagesKeepLoginRedirectAndFacadeBehavior(string path, bool facade)
    {
        ConfigureAuthentication(facade);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request);
        if (facade && path == "/")
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("404 Not Found", await response.Content.ReadAsStringAsync());
            Assert.Null(response.Headers.Location);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(facade ? "/" : "/login", response.Headers.Location?.OriginalString);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PluginAssetsStillRejectUnauthenticatedHtmlWhileStaticScriptsRemainAccessible(bool facade)
    {
        ConfigureAuthentication(facade);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/plugin-assets/diagnostic/page.js");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request);
        await AssertApiUnauthorized(response);
        using var asset = await client.GetAsync("/assets/diagnostic.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal(new string('a', 4096), await asset.Content.ReadAsStringAsync());
        Assert.Null(asset.Headers.Location);
    }

    private void ConfigureAuthentication(bool facade, bool configured = true)
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        WinPilotSettings.Update(paths.ConfigurationFile, current => current with { UnauthenticatedWebPageEnabled = facade });
        if (configured) app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
    }

    private static async Task AssertApiUnauthorized(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Empty(response.Content.Headers.Allow);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task ApiNamesWithExtensionsStillRequireAuthentication(string method)
    {
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        using var anonymous = new HttpRequestMessage(new HttpMethod(method), "/api/protected/task.json");
        using var rejected = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var login = await client.PostAsJsonAsync("/login", new { password = "a secure passphrase" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var authenticated = new HttpRequestMessage(new HttpMethod(method), "/api/protected/task.json");
        authenticated.Headers.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        using var accepted = await client.SendAsync(authenticated);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public async Task PluginAssets_RequireSessionWhenPasswordIsConfigured()
    {
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/plugin-assets/diagnostic/page.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/assets/diagnostic.js")).StatusCode);

        var session = app.Services.GetRequiredService<SessionManager>().Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/plugin-assets/diagnostic/page.js");
        request.Headers.Add("Cookie", $"WinPilot.Session={session}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new string('b', 4096), await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/assets/diagnostic.js", false)]
    [InlineData("/plugin-assets/diagnostic/page.js", true)]
    public async Task StaticScriptsAndApi_UseGzip(string path, bool requiresSession)
    {
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        if (requiresSession)
            request.Headers.Add("Cookie", $"WinPilot.Session={app.Services.GetRequiredService<SessionManager>().Create()}");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var decompressed = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressed);
        Assert.Equal(new string(requiresSession ? 'b' : 'a', 4096), await reader.ReadToEndAsync());

        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        apiRequest.Headers.AcceptEncoding.ParseAdd("gzip");
        apiRequest.Headers.Add("Cookie", $"WinPilot.Session={app.Services.GetRequiredService<SessionManager>().Create()}");
        using var apiResponse = await client.SendAsync(apiRequest);
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);
        Assert.Equal("gzip", Assert.Single(apiResponse.Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task WebSocketAndSse_UnauthenticatedRequestsReturn401WithoutHtmlFallback()
    {
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");

        using var webSocket = new HttpRequestMessage(HttpMethod.Get, "/api/terminal/ws");
        webSocket.Headers.Add("Connection", "Upgrade");
        webSocket.Headers.Add("Upgrade", "websocket");
        using var webSocketResponse = await client.SendAsync(webSocket);
        Assert.Equal(HttpStatusCode.Unauthorized, webSocketResponse.StatusCode);
        Assert.NotEqual("text/html", webSocketResponse.Content.Headers.ContentType?.MediaType);

        using var sse = new HttpRequestMessage(HttpMethod.Get, "/api/test-events");
        sse.Headers.Accept.ParseAdd("text/event-stream");
        using var sseResponse = await client.SendAsync(sse);
        Assert.Equal(HttpStatusCode.Unauthorized, sseResponse.StatusCode);
        Assert.NotEqual("text/html", sseResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SessionRevocation_EndsExistingWebSocketAndSseConnections()
    {
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        var sessions = app.Services.GetRequiredService<SessionManager>();
        var session = sessions.Create();

        var webSocketClient = app.GetTestServer().CreateWebSocketClient();
        webSocketClient.ConfigureRequest = request => request.Headers.Cookie = $"WinPilot.Session={session}";
        using var socket = await webSocketClient.ConnectAsync(new Uri("ws://localhost/api/terminal/ws"), CancellationToken.None);

        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, "/api/test-events");
        sseRequest.Headers.Add("Cookie", $"WinPilot.Session={session}");
        sseRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        await using var stream = await sseResponse.Content.ReadAsStreamAsync();
        var buffer = new byte[64];
        Assert.True(await stream.ReadAsync(buffer) > 0);

        sessions.RevokeAll();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = socket.ReceiveAsync(new byte[64], timeout.Token);
        var sseRead = stream.ReadAsync(buffer, timeout.Token).AsTask();
        Assert.Same(receive, await Task.WhenAny(receive, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)));
        Assert.Same(sseRead, await Task.WhenAny(sseRead, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)));
    }

    [Fact]
    public async Task AuthConfigurationLoss_RevokesSessionAndEndsExistingLongConnections()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        var originalAuth = File.ReadAllBytes(paths.AuthenticationFile);
        var sessions = app.Services.GetRequiredService<SessionManager>();
        var session = sessions.Create();

        var webSocketClient = app.GetTestServer().CreateWebSocketClient();
        webSocketClient.ConfigureRequest = request => request.Headers.Cookie = $"WinPilot.Session={session}";
        using var socket = await webSocketClient.ConnectAsync(new Uri("ws://localhost/api/terminal/ws"), CancellationToken.None);
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, "/api/test-events");
        sseRequest.Headers.Add("Cookie", $"WinPilot.Session={session}");
        sseRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        await using var stream = await sseResponse.Content.ReadAsStreamAsync();
        Assert.True(await stream.ReadAsync(new byte[64]) > 0);
        using var safeModeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/security/safe-mode/events");
        safeModeRequest.Headers.Add("Cookie", $"WinPilot.Session={session}");
        using var safeModeResponse = await client.SendAsync(safeModeRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, safeModeResponse.StatusCode);
        await using var safeModeStream = await safeModeResponse.Content.ReadAsStreamAsync();
        Assert.True(await safeModeStream.ReadAsync(new byte[64]) > 0);

        File.Delete(paths.AuthenticationFile);
        using var trigger = new HttpRequestMessage(HttpMethod.Get, "/api/programs");
        trigger.Headers.Add("X-Test-Remote-IP", "192.168.1.10");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(trigger)).StatusCode);
        Assert.True(app.Services.GetRequiredService<SafeModeService>().Enabled);
        Assert.False(sessions.Validate(session));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = socket.ReceiveAsync(new byte[64], timeout.Token);
        var sseRead = stream.ReadAsync(new byte[64], timeout.Token).AsTask();
        var safeModeRead = safeModeStream.ReadAsync(new byte[64], timeout.Token).AsTask();
        Assert.Same(receive, await Task.WhenAny(receive, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)));
        Assert.Same(sseRead, await Task.WhenAny(sseRead, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)));
        Assert.Same(safeModeRead, await Task.WhenAny(safeModeRead, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)));

        File.WriteAllBytes(paths.AuthenticationFile, originalAuth);
        using var oldSessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        oldSessionRequest.Headers.Add("Cookie", $"WinPilot.Session={session}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(oldSessionRequest)).StatusCode);
    }

    [Fact]
    public async Task SafeMode_EndsExistingTerminalWebSocketAndEnforcesApiPolicy()
    {
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        var session = app.Services.GetRequiredService<SessionManager>().Create();
        var webSocketClient = app.GetTestServer().CreateWebSocketClient();
        webSocketClient.ConfigureRequest = request => request.Headers.Cookie = $"WinPilot.Session={session}";
        using var socket = await webSocketClient.ConnectAsync(new Uri("ws://localhost/api/terminal/ws"), CancellationToken.None);

        app.Services.GetRequiredService<SafeModeService>().SetEnabled(true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = socket.ReceiveAsync(new byte[64], timeout.Token);
        Assert.Same(receive, await Task.WhenAny(receive, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)));

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/system/status");
        allowedRequest.Headers.Add("Cookie", $"WinPilot.Session={session}");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(allowedRequest)).StatusCode);

        using var channelsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/notifications/channels");
        channelsRequest.Headers.Add("Cookie", $"WinPilot.Session={session}");
        using var channelsResponse = await client.SendAsync(channelsRequest);
        Assert.Equal(HttpStatusCode.OK, channelsResponse.StatusCode);

        using var settingsWrite = new HttpRequestMessage(HttpMethod.Patch, "/api/settings/notifications/telegram");
        settingsWrite.Headers.Add("Cookie", $"WinPilot.Session={session}");
        using var writeResponse = await client.SendAsync(settingsWrite);
        Assert.Equal(HttpStatusCode.Forbidden, writeResponse.StatusCode);

        using var unrestrictedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        unrestrictedRequest.Headers.Add("Cookie", $"WinPilot.Session={session}");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(unrestrictedRequest)).StatusCode);

        using var upgradeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/terminal/ws");
        upgradeRequest.Headers.Add("Cookie", $"WinPilot.Session={session}");
        upgradeRequest.Headers.Add("Connection", "Upgrade");
        upgradeRequest.Headers.Add("Upgrade", "websocket");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(upgradeRequest)).StatusCode);
    }

    [Fact]
    public void SafeMode_DoesNotCloseAllowedPluginFeatureSessions()
    {
        var pageSessions = app.Services.GetRequiredService<PluginPageSessionManager>();
        var pageId = pageSessions.Open("test-plugin", "page", _ => { });

        app.Services.GetRequiredService<SafeModeService>().SetEnabled(true);

        pageSessions.Touch(pageId);
    }

    [Theory]
    [InlineData("GET", "/api/files/roots")]
    [InlineData("HEAD", "/api/files/roots")]
    [InlineData("POST", "/api/files/upload")]
    [InlineData("GET", "/api/terminal/ws")]
    [InlineData("POST", "/api/desktop/keyboard")]
    [InlineData("GET", "/api/programs")]
    [InlineData("GET", "/API/PROGRAMS/")]
    [InlineData("GET", "/api/programs/users")]
    [InlineData("HEAD", "/api/programs/users")]
    [InlineData("POST", "/api/programs/usage")]
    [InlineData("POST", "/api/programs/close")]
    [InlineData("POST", "/api/programs/launch")]
    [InlineData("POST", "/api/programs/tags")]
    [InlineData("PUT", "/api/programs/tags/test")]
    [InlineData("DELETE", "/api/programs/tags/test")]
    [InlineData("POST", "/api/tasks")]
    [InlineData("PUT", "/api/tasks/test")]
    [InlineData("DELETE", "/api/tasks/test")]
    [InlineData("PATCH", "/api/settings/general/port")]
    [InlineData("PATCH", "/api/settings/notifications/telegram")]
    [InlineData("POST", "/api/notifications/test")]
    [InlineData("POST", "/api/notifications/qqbot/bind-user")]
    [InlineData("POST", "/api/notifications/qqbot/bind-group")]
    [InlineData("PUT", "/api/settings/security/password")]
    [InlineData("PUT", "/api/settings/security/notifications")]
    [InlineData("PUT", "/api/settings/updates")]
    [InlineData("DELETE", "/api/plugins/test")]
    [InlineData("PUT", "/api/plugins/test/configuration")]
    [InlineData("PUT", "/api/plugins/test/settings/display/status")]
    [InlineData("PUT", "/api/plugins/test/settings/display/features")]
    [InlineData("PUT", "/api/plugins/test/settings/display/configuration")]
    public async Task SafeMode_BlocksRestrictedFeatures(string method, string path)
    {
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        var session = app.Services.GetRequiredService<SessionManager>().Create();
        app.Services.GetRequiredService<SafeModeService>().SetEnabled(true);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT" or "PATCH") request.Content = JsonContent.Create(new { });
        request.Headers.Add("Cookie", $"WinPilot.Session={session}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/settings/test")]
    [InlineData("HEAD", "/api/settings/test")]
    [InlineData("POST", "/api/notifications/diagnostic")]
    [InlineData("HEAD", "/api/plugins/test/readme")]
    [InlineData("GET", "/api/plugins/options/settings/notificationChannels")]
    [InlineData("GET", "/api/programs/tags")]
    [InlineData("POST", "/api/programs/tags/test/launch")]
    [InlineData("GET", "/api/tasks")]
    [InlineData("POST", "/api/tasks/test/run")]
    [InlineData("PATCH", "/api/tasks/test/enabled")]
    [InlineData("GET", "/api/plugins/test/configuration")]
    [InlineData("POST", "/api/plugins/test/enable")]
    [InlineData("POST", "/api/plugins/scan")]
    [InlineData("POST", "/api/plugin-page-sessions/test/messages")]
    [InlineData("DELETE", "/api/plugin-page-sessions/test")]
    [InlineData("PUT", "/api/protected/test")]
    public async Task SafeMode_AllowsOtherAuthenticatedOperations(string method, string path)
    {
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        var session = app.Services.GetRequiredService<SessionManager>().Create();
        app.Services.GetRequiredService<SafeModeService>().SetEnabled(true);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Cookie", $"WinPilot.Session={session}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var anonymousResponse = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
    }

    [Fact]
    public async Task SafeNotificationReadsReturnOnlyChannelSwitchesAndRestoreFullSettings()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        WinPilotSettings.Update(paths.ConfigurationFile, current => current with
        {
            Notifications = current.Notifications with
            {
                TelegramEnabled = true, TelegramBotToken = "private-token", TelegramChatId = "private-chat",
                QQBotEnabled = true, QQBotClientSecret = "private-secret"
            }
        });
        var safeMode = app.Services.GetRequiredService<SafeModeService>();
        safeMode.SetEnabled(true);
        using var response = await client.GetAsync("/api/settings/notifications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "feishuEnabled", "oneBotEnabled", "qqBotEnabled", "telegramEnabled", "webhookEnabled", "windowsEnabled", "workWeixinEnabled" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.True(json.RootElement.GetProperty("telegramEnabled").GetBoolean());
        Assert.True(json.RootElement.GetProperty("qqBotEnabled").GetBoolean());
        safeMode.SetEnabled(false);
        using var restored = await client.GetAsync("/api/settings/notifications");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Contains("private-token", await restored.Content.ReadAsStringAsync());
        Assert.Contains("private-secret", await restored.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("general")]
    [InlineData("notifications")]
    public void WholeSettingsWritesAreNotAvailable(string group)
    {
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText == $"/api/settings/{group}").ToArray();
        Assert.Contains(routes, endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        Assert.DoesNotContain(routes, endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
    }

    [Fact]
    public async Task ConcurrentSettingsEndpointsPreserveOtherConfigurationSections()
    {
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");
        var file = app.Services.GetRequiredService<PortableDataPaths>().ConfigurationFile;
        WinPilotSettings.Update(file, current => current with
        {
            Notifications = current.Notifications with { QQBotAppId = "app" },
            CommandBots = current.CommandBots with { TelegramEnabled = true, TelegramPrefix = "/" }
        });
        var responses = await Task.WhenAll(
            client.PatchAsJsonAsync("/api/settings/general/port", new { port = 9456 }),
            client.PatchAsJsonAsync("/api/settings/general/lan-access", new { enabled = true }),
            client.PutAsJsonAsync("/api/settings/security/notifications", new SecurityNotificationSettings
            { Events = [SecurityEventType.PasswordChanged] }));
        try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }
        var saved = WinPilotSettings.LoadAndInitialize(file);
        Assert.Equal(9456, saved.Port);
        Assert.True(saved.LanAccessEnabled);
        Assert.Equal("app", saved.Notifications.QQBotAppId);
        Assert.True(saved.CommandBots.TelegramEnabled);
        Assert.Equal("/", saved.CommandBots.TelegramPrefix);
        Assert.Equal(new[] { SecurityEventType.PasswordChanged, SecurityEventType.PasswordSet }, saved.SecurityNotifications.Events);
    }

    [Fact]
    public async Task PasswordState_AllowsUnauthenticatedLanWhenEnabledAndEnforcesAuthenticationAfterPasswordSet()
    {
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        var enableLan = await client.PatchAsJsonAsync("/api/settings/general/lan-access", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, enableLan.StatusCode);
        Assert.True(WinPilotSettings.LoadAndInitialize(app.Services.GetRequiredService<PortableDataPaths>().ConfigurationFile).LanAccessEnabled);

        client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.168.1.10");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.False(app.Services.GetRequiredService<SafeModeService>().Enabled);

        client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");
        var setPassword = await client.PutAsJsonAsync("/api/settings/security/password", new { currentPassword = "", newPassword = "a secure passphrase", confirmation = "a secure passphrase" });
        Assert.Equal(HttpStatusCode.NoContent, setPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/login", new { password = "wrong password" })).StatusCode);

        var login = await client.PostAsJsonAsync("/login", new { password = "a secure passphrase" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        var missingApi = await client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missingApi.StatusCode);
        var missingApiProblem = await missingApi.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("API 不存在", missingApiProblem?.Title);

        var changed = await client.PutAsJsonAsync("/api/settings/security/password", new { currentPassword = "a secure passphrase", newPassword = "another secure passphrase", confirmation = "another secure passphrase" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/login", new { password = "another secure passphrase" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task DeletingOrCorruptingAuthFile_RevokesSessionsAndAllowsManualSafeModeExit()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var auth = app.Services.GetRequiredService<AuthStore>();
        var sessions = app.Services.GetRequiredService<SessionManager>();
        var safeMode = app.Services.GetRequiredService<SafeModeService>();
        auth.SetInitialPassword("a secure passphrase");
        var originalSession = sessions.Create();

        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.168.1.10");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/protected")).StatusCode);
        File.Delete(paths.AuthenticationFile);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/programs")).StatusCode);
        Assert.True(safeMode.Enabled);
        Assert.Contains("密码配置缺失或损坏，已自动开启安全模式", ReadActiveLog(paths.LogsDirectory));
        safeMode.SetEnabled(false);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.False(safeMode.Enabled);
        Assert.Equal(1, ReadActiveLog(paths.LogsDirectory).Split("密码配置缺失或损坏，已自动开启安全模式").Length - 1);
        Assert.False(sessions.Validate(originalSession));

        client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings/security/password", new { currentPassword = "", newPassword = "another secure passphrase", confirmation = "another secure passphrase" })).StatusCode);

        var restoredSession = sessions.Create();
        using var restoredRequest = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        restoredRequest.Headers.Add("Cookie", $"WinPilot.Session={restoredSession}");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(restoredRequest)).StatusCode);

        File.WriteAllText(paths.AuthenticationFile, "{ invalid json }");
        client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.168.1.10");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/programs")).StatusCode);
        Assert.True(safeMode.Enabled);
        safeMode.SetEnabled(false);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.False(safeMode.Enabled);
        Assert.Equal(2, ReadActiveLog(paths.LogsDirectory).Split("密码配置缺失或损坏，已自动开启安全模式").Length - 1);
        Assert.False(sessions.Validate(restoredSession));
        client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings/security/password", new { currentPassword = "", newPassword = "a third secure passphrase", confirmation = "a third secure passphrase" })).StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthConfigurationLossWithFailedProtectionRejectsRestrictedRequests(bool corrupt, bool subsequent)
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var auth = app.Services.GetRequiredService<AuthStore>();
        var sessions = app.Services.GetRequiredService<SessionManager>();
        var safeMode = app.Services.GetRequiredService<SafeModeService>();
        auth.SetInitialPassword("a secure passphrase");
        var originalSession = sessions.Create();
        using var beforeLoss = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, beforeLoss.StatusCode);
        Assert.False(safeMode.Enabled);
        Assert.False(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SafeModeEnabled);
        if (corrupt) File.WriteAllText(paths.AuthenticationFile, "{ invalid json }");
        else File.Delete(paths.AuthenticationFile);
        Assert.False(auth.IsConfigured);

        HttpStatusCode firstStatus;
        bool firstProtectionEnabled;
        using (var lockedFile = new FileStream(paths.ConfigurationFile + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var first = await client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" });
            firstStatus = first.StatusCode;
            firstProtectionEnabled = safeMode.Enabled;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
            Assert.Equal("application/problem+json", first.Content.Headers.ContentType?.MediaType);
            var body = await first.Content.ReadAsStringAsync();
            using var problem = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal("安全保护未能完成", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal("安全模式已开启，请通过本机检查配置和程序日志", problem.RootElement.GetProperty("detail").GetString());
            Assert.DoesNotContain(root, body);
            Assert.DoesNotContain("IOException", body);
            Assert.True(safeMode.Enabled);
            Assert.True(safeMode.RestrictedOperationsCancellation.IsCancellationRequested);
            Assert.False(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SafeModeEnabled);
            using var stateResponse = await client.GetAsync("/api/security/safe-mode");
            Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
            using var state = System.Text.Json.JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync());
            Assert.True(state.RootElement.GetProperty("enabled").GetBoolean());
            using var restricted = await client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" });
            Assert.Equal(HttpStatusCode.Forbidden, restricted.StatusCode);
            Assert.False(sessions.Validate(originalSession));
            var log = ReadActiveLog(paths.LogsDirectory);
            Assert.Contains("密码配置缺失或损坏，安全模式已开启，但配置保存失败", log);
            Assert.Contains("IOException", log);
        }

        WinPilotSettings.Update(paths.ConfigurationFile, current => current);
        using var later = await client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" });
        var failedProtectionAttempts = ReadActiveLog(paths.LogsDirectory)
            .Split("密码配置缺失或损坏，安全模式已开启，但配置保存失败", StringSplitOptions.None).Length - 1;
        Assert.Equal(HttpStatusCode.Forbidden, later.StatusCode);
        Assert.True(safeMode.Enabled);
        Assert.Equal(1, failedProtectionAttempts);
        var status = subsequent ? later.StatusCode : firstStatus;
        Assert.False((int)status is >= 200 and < 300,
            $"首次请求：{(int)firstStatus}，首次安全模式：{firstProtectionEnabled}；解除保存故障后的后续请求：{(int)later.StatusCode}，安全模式：{safeMode.Enabled}；保护失败记录：{failedProtectionAttempts}");
    }

    [Fact]
    public async Task AuthConfigurationLossBlocksConcurrentRequestsBeforeRevocationCompletes()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var sessions = app.Services.GetRequiredService<SessionManager>();
        var safeMode = app.Services.GetRequiredService<SafeModeService>();
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        using var warmup = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, warmup.StatusCode);
        var session = sessions.Create();
        Assert.True(sessions.TryValidate(session, out var invalidated));
        using var release = new ManualResetEventSlim();
        var revoking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = invalidated.Register(() =>
        {
            revoking.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("认证并发测试未释放撤销握手");
        });
        File.Delete(paths.AuthenticationFile);
        var first = Task.Run(() => client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" }));
        try
        {
            await revoking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(safeMode.Enabled);
            using var concurrent = await client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.Forbidden, concurrent.StatusCode);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            release.Set();
            using var completed = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.Forbidden, completed.StatusCode);
        }
        Assert.True(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SafeModeEnabled);
        Assert.False(sessions.Validate(session));
    }

    [Fact]
    public async Task AuthProtectionPersistenceFailureAllowsExplicitManualExitWithoutReactivation()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var safeMode = app.Services.GetRequiredService<SafeModeService>();
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        using var warmup = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, warmup.StatusCode);
        File.Delete(paths.AuthenticationFile);
        using (var lockedFile = new FileStream(paths.ConfigurationFile + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var failed = await client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.True(safeMode.Enabled);
        }
        safeMode.SetEnabled(false);
        Assert.False(safeMode.Enabled);
        Assert.False(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SafeModeEnabled);
        using var allowed = await client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" });
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.False(safeMode.Enabled);
        Assert.Equal(1, ReadActiveLog(paths.LogsDirectory).Split("安全模式已开启，但配置保存失败", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task FirstUseWithoutPasswordDoesNotAttemptAuthenticationProtectionPersistence()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var safeMode = app.Services.GetRequiredService<SafeModeService>();
        Assert.False(app.Services.GetRequiredService<AuthStore>().IsConfigured);
        Assert.False(safeMode.Enabled);
        using var lockedFile = new FileStream(paths.ConfigurationFile + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var allowed = await client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" });
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.False(safeMode.Enabled);
        Assert.DoesNotContain("安全模式已开启，但配置保存失败", ReadActiveLog(paths.LogsDirectory));
    }

    [Fact]
    public async Task AuthProtectionCancelsAndPublishesStateEvenWhenCancellationCallbackFails()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var safeMode = app.Services.GetRequiredService<SafeModeService>();
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        using var warmup = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, warmup.StatusCode);
        await using var states = safeMode.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await states.MoveNextAsync());
        Assert.False(states.Current);
        var restrictedOperations = safeMode.RestrictedOperationsCancellation;
        using var registration = restrictedOperations.Register(() => throw new InvalidOperationException("保护回调测试"));
        File.Delete(paths.AuthenticationFile);
        using var restricted = await client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" });
        Assert.Equal(HttpStatusCode.Forbidden, restricted.StatusCode);
        Assert.True(safeMode.Enabled);
        Assert.True(restrictedOperations.IsCancellationRequested);
        Assert.True(await states.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(states.Current);
        Assert.True(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SafeModeEnabled);
        var log = ReadActiveLog(paths.LogsDirectory);
        Assert.Contains("取消受限操作的回调失败", log);
        Assert.Contains("保护回调测试", log);
    }

    [Fact]
    public async Task ManualSafeModeExitDuringAuthenticationProtectionDoesNotRestoreStaleState()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var sessions = app.Services.GetRequiredService<SessionManager>();
        var safeMode = app.Services.GetRequiredService<SafeModeService>();
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        using var warmup = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, warmup.StatusCode);
        Assert.True(sessions.TryValidate(sessions.Create(), out var invalidated));
        using var release = new ManualResetEventSlim();
        var revoking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = invalidated.Register(() =>
        {
            revoking.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("认证解除测试未释放撤销握手");
        });
        File.Delete(paths.AuthenticationFile);
        var first = Task.Run(() => client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" }));
        try
        {
            await revoking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(safeMode.Enabled);
            safeMode.SetEnabled(false);
        }
        finally
        {
            release.Set();
            using var completed = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        }
        Assert.False(safeMode.Enabled);
        Assert.False(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SafeModeEnabled);
        using var allowed = await client.PostAsJsonAsync("/api/programs/launch", new { programPath = "test.exe" });
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task PasswordSetImmediatelyMarksConfigurationValidForLossDetection()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var sessions = app.Services.GetRequiredService<SessionManager>();
        using var firstUse = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        firstUse.Headers.Add("X-Test-Remote-IP", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(firstUse)).StatusCode);

        using var setPassword = new HttpRequestMessage(HttpMethod.Put, "/api/settings/security/password")
        {
            Content = JsonContent.Create(new { currentPassword = "", newPassword = "a secure passphrase", confirmation = "a secure passphrase" })
        };
        setPassword.Headers.Add("X-Test-Remote-IP", "127.0.0.1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(setPassword)).StatusCode);

        var session = sessions.Create();
        File.Delete(paths.AuthenticationFile);
        using var lanRequest = new HttpRequestMessage(HttpMethod.Get, "/api/programs");
        lanRequest.Headers.Add("X-Test-Remote-IP", "192.168.1.10");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(lanRequest)).StatusCode);
        Assert.True(app.Services.GetRequiredService<SafeModeService>().Enabled);
        Assert.False(sessions.Validate(session));
    }

    [Fact]
    public async Task SecurityNotificationSettings_HaveDefaultsAndValidateOneChannel()
    {
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");
        using var defaultsResponse = await client.GetAsync("/api/settings/security/notifications");
        defaultsResponse.EnsureSuccessStatusCode();
        using var defaults = System.Text.Json.JsonDocument.Parse(await defaultsResponse.Content.ReadAsStringAsync());
        Assert.Equal("", defaults.RootElement.GetProperty("channel").GetString());
        Assert.Equal(SecurityEventDefaults.Notified.Select(type => type.ToString()), defaults.RootElement.GetProperty("events").EnumerateArray().Select(item => item.GetString()));

        using var invalid = await client.PutAsJsonAsync("/api/settings/security/notifications", new { events = new[] { "PasswordSet" }, channel = "unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var saved = await client.PutAsJsonAsync("/api/settings/security/notifications", new { events = new[] { "PasswordSet", "LoginSourceLocked" }, channel = "telegram" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var savedJson = System.Text.Json.JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        Assert.Equal("telegram", savedJson.RootElement.GetProperty("channel").GetString());
        Assert.Equal(2, savedJson.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task WebFacade_ShieldsUnauthenticatedNavigationAndRequiresAnEntryGrant()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var auth = app.Services.GetRequiredService<AuthStore>();
        auth.SetInitialPassword("a secure passphrase");
        new WinPilotSettings { Port = 9123, UnauthenticatedWebPageEnabled = true }.Save(paths.ConfigurationFile);

        using var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.True(root.Headers.CacheControl?.NoStore);
        var defaultPage = await root.Content.ReadAsStringAsync();
        Assert.Contains("404 Not Found", defaultPage);
        Assert.Contains("document.addEventListener('pointerdown'", defaultPage);
        Assert.True(File.Exists(paths.WebIndexFile));
        var visitor = root.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(WebFacadeService.VisitorCookieName, StringComparison.Ordinal)).Split(';', 2)[0];

        using var loginRequest = new HttpRequestMessage(HttpMethod.Get, "/login");
        loginRequest.Headers.Accept.ParseAdd("text/html");
        using var directLogin = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Found, directLogin.StatusCode);
        Assert.Equal("/", directLogin.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/login", new { password = "a secure passphrase" })).StatusCode);

        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, "/abc");
        pageRequest.Headers.Accept.ParseAdd("text/html");
        using var pageRedirect = await client.SendAsync(pageRequest);
        Assert.Equal(HttpStatusCode.Found, pageRedirect.StatusCode);
        Assert.Equal("/", pageRedirect.Headers.Location?.OriginalString);

        using var api = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.DoesNotContain("Welcome", await api.Content.ReadAsStringAsync());

        Directory.CreateDirectory(Path.Combine(paths.WebDirectory, "css"));
        File.WriteAllText(Path.Combine(paths.WebDirectory, "css", "site.css"), "body { color: red; }");
        using var resource = await client.GetAsync("/css/site.css");
        Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
        Assert.Equal("body { color: red; }", await resource.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/User/auth.json")).StatusCode);

        using var entryRequest = new HttpRequestMessage(HttpMethod.Post, "/_manage");
        entryRequest.Headers.Add("Cookie", visitor);
        using var entry = await client.SendAsync(entryRequest);
        Assert.Equal(HttpStatusCode.NoContent, entry.StatusCode);
        var managementEntry = entry.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(WebFacadeService.ManagementEntryCookieName, StringComparison.Ordinal)).Split(';', 2)[0];

        using var grantedLoginRequest = new HttpRequestMessage(HttpMethod.Get, "/login");
        grantedLoginRequest.Headers.Add("Cookie", managementEntry);
        using var grantedLogin = await client.SendAsync(grantedLoginRequest);
        Assert.Equal(HttpStatusCode.OK, grantedLogin.StatusCode);

        using var authenticatedLogin = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = JsonContent.Create(new { password = "a secure passphrase" })
        };
        authenticatedLogin.Headers.Add("Cookie", managementEntry);
        using var login = await client.SendAsync(authenticatedLogin);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var session = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("WinPilot.Session", StringComparison.Ordinal)).Split(';', 2)[0];
        using var authenticatedRoot = new HttpRequestMessage(HttpMethod.Get, "/");
        authenticatedRoot.Headers.Add("Cookie", session);
        Assert.Equal("home", await (await client.SendAsync(authenticatedRoot)).Content.ReadAsStringAsync());

        File.WriteAllText(paths.WebIndexFile, "custom facade");
        app.Services.GetRequiredService<WebFacadeService>().EnsureDefaultPage();
        Assert.Equal("custom facade", File.ReadAllText(paths.WebIndexFile));
    }

    [Theory]
    [InlineData("/", "gzip")]
    [InlineData("/", "br")]
    [InlineData("/site.css", "gzip")]
    [InlineData("/site.css", "br")]
    [InlineData("/site.js", "gzip")]
    [InlineData("/site.js", "br")]
    [InlineData("/readme.txt", "gzip")]
    [InlineData("/readme.txt", "br")]
    public async Task WebFacade_CompressesPagesAndTextResources(string path, string encoding)
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        new WinPilotSettings { UnauthenticatedWebPageEnabled = true }.Save(paths.ConfigurationFile);
        Directory.CreateDirectory(paths.WebDirectory);
        var content = new string('a', 4096);
        File.WriteAllText(path == "/" ? paths.WebIndexFile : Path.Combine(paths.WebDirectory, path.TrimStart('/')), content);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.AcceptEncoding.ParseAdd(encoding);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(encoding, response.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        await using var compressed = await response.Content.ReadAsStreamAsync();
        await using Stream decoded = encoding == "br"
            ? new BrotliStream(compressed, CompressionMode.Decompress)
            : new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(decoded);
        Assert.Equal(content, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task WebFacade_ResourcesRevalidateAndReturnModifiedContent()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        app.Services.GetRequiredService<AuthStore>().SetInitialPassword("a secure passphrase");
        new WinPilotSettings { UnauthenticatedWebPageEnabled = true }.Save(paths.ConfigurationFile);
        Directory.CreateDirectory(paths.WebDirectory);
        var file = Path.Combine(paths.WebDirectory, "site.css");
        File.WriteAllText(file, "body { color: red; }");
        using var first = await client.GetAsync("/site.css");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.Private);
        Assert.True(first.Headers.CacheControl?.NoCache);
        Assert.NotNull(first.Headers.ETag);
        Assert.NotNull(first.Content.Headers.LastModified);
        using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, "/site.css");
        unchangedRequest.Headers.IfNoneMatch.Add(first.Headers.ETag);
        using var unchanged = await client.SendAsync(unchangedRequest);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        using var modifiedSinceRequest = new HttpRequestMessage(HttpMethod.Get, "/site.css");
        modifiedSinceRequest.Headers.IfModifiedSince = first.Content.Headers.LastModified;
        using var modifiedSince = await client.SendAsync(modifiedSinceRequest);
        Assert.Equal(HttpStatusCode.NotModified, modifiedSince.StatusCode);
        Assert.Empty(await modifiedSince.Content.ReadAsByteArrayAsync());
        File.WriteAllText(file, "body { color: green; }");
        using var changedRequest = new HttpRequestMessage(HttpMethod.Get, "/site.css");
        changedRequest.Headers.IfNoneMatch.Add(first.Headers.ETag);
        using var changed = await client.SendAsync(changedRequest);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(first.Headers.ETag, changed.Headers.ETag);
        Assert.Equal("body { color: green; }", await changed.Content.ReadAsStringAsync());
    }

    private static string ReadActiveLog(string logsDirectory)
    {
        using var stream = new FileStream(Directory.GetFiles(logsDirectory, "winpilot*.log").Single(),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            try { await app.StopAsync(); }
            finally { await app.DisposeAsync(); }
        }
        logProvider?.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

public sealed class TestNotificationService : INotificationService
{
    public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default, string? channel = null, bool ignoreChannelEnabled = false) => Task.FromResult<IReadOnlyList<NotificationChannelResult>>([]);
}
