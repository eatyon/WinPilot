using WinPilot.Core;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Features.Security;
using WinPilot.Features.Notifications;
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

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddResponseCompression(options =>
            options.MimeTypes = ["text/html", "text/css", "text/javascript", "application/javascript", "application/json", "application/problem+json", "image/svg+xml"]);
        var paths = PortableDataPaths.Create(root);
        logProvider = new RollingFileLoggerProvider(paths.LogsDirectory);
        builder.Logging.AddProvider(logProvider);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddSingleton<INotificationService, TestNotificationService>();
        builder.Services.AddSingleton<SecurityEventNotifier>();
        builder.Services.AddSingleton<WebFacadeService>();
        builder.Services.AddSingleton<TerminalSessionManager>();
        builder.Services.AddSingleton<PluginPageSessionManager>();
        builder.Services.AddSingleton<SafeModeService>();
        builder.Services.AddSingleton<UpdateCheckService>();
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Remote-IP"].FirstOrDefault() ?? "192.168.1.10");
            await next();
        });
        app.UseResponseCompression();
        app.UseMiddleware<AuthenticationMiddleware>();
        app.UseMiddleware<SafeModeMiddleware>();
        app.UseWebSockets();
        app.MapHealthEndpoint();
        app.MapUpdateEndpoints();
        app.MapAuthenticationEndpoints();
        app.MapSafeModeEndpoints();
        app.MapSettingsEndpoints(paths.ConfigurationFile, new StartupTaskService());
        app.MapTerminalEndpoints();
        app.MapGet("/api/protected", () => Results.Ok(new { ok = true }));
        app.MapMethods("/api/protected/{name}", ["GET", "PUT", "DELETE"], () => Results.NoContent());
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
        app.MapFallback("/api/{**path}", () =>
            Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "API 不存在"));
        await app.StartAsync();
        client = app.GetTestClient();
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
        using var trigger = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
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

        using var blockedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        blockedRequest.Headers.Add("Cookie", $"WinPilot.Session={session}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(blockedRequest)).StatusCode);

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
            client.PatchAsJsonAsync("/api/settings/port", new { port = 9456 }),
            client.PatchAsJsonAsync("/api/settings/lan-access", new { enabled = true }),
            client.PutAsJsonAsync("/api/security/notifications", new SecurityNotificationSettings
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
        var enableLan = await client.PatchAsJsonAsync("/api/settings/lan-access", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, enableLan.StatusCode);
        Assert.True(WinPilotSettings.LoadAndInitialize(app.Services.GetRequiredService<PortableDataPaths>().ConfigurationFile).LanAccessEnabled);

        client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.168.1.10");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.False(app.Services.GetRequiredService<SafeModeService>().Enabled);

        client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");
        var setPassword = await client.PutAsJsonAsync("/api/security/password", new { currentPassword = "", newPassword = "a secure passphrase", confirmation = "a secure passphrase" });
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

        var changed = await client.PutAsJsonAsync("/api/security/password", new { currentPassword = "a secure passphrase", newPassword = "another secure passphrase", confirmation = "another secure passphrase" });
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
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/protected")).StatusCode);
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
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/security/password", new { currentPassword = "", newPassword = "another secure passphrase", confirmation = "another secure passphrase" })).StatusCode);

        var restoredSession = sessions.Create();
        using var restoredRequest = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        restoredRequest.Headers.Add("Cookie", $"WinPilot.Session={restoredSession}");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(restoredRequest)).StatusCode);

        File.WriteAllText(paths.AuthenticationFile, "{ invalid json }");
        client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "192.168.1.10");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.True(safeMode.Enabled);
        safeMode.SetEnabled(false);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.False(safeMode.Enabled);
        Assert.Equal(2, ReadActiveLog(paths.LogsDirectory).Split("密码配置缺失或损坏，已自动开启安全模式").Length - 1);
        Assert.False(sessions.Validate(restoredSession));
        client.DefaultRequestHeaders.Remove("X-Test-Remote-IP");
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/protected")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/security/password", new { currentPassword = "", newPassword = "a third secure passphrase", confirmation = "a third secure passphrase" })).StatusCode);
    }

    [Fact]
    public async Task PasswordSetImmediatelyMarksConfigurationValidForLossDetection()
    {
        var paths = app.Services.GetRequiredService<PortableDataPaths>();
        var sessions = app.Services.GetRequiredService<SessionManager>();
        using var firstUse = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        firstUse.Headers.Add("X-Test-Remote-IP", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(firstUse)).StatusCode);

        using var setPassword = new HttpRequestMessage(HttpMethod.Put, "/api/security/password")
        {
            Content = JsonContent.Create(new { currentPassword = "", newPassword = "a secure passphrase", confirmation = "a secure passphrase" })
        };
        setPassword.Headers.Add("X-Test-Remote-IP", "127.0.0.1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(setPassword)).StatusCode);

        var session = sessions.Create();
        File.Delete(paths.AuthenticationFile);
        using var lanRequest = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        lanRequest.Headers.Add("X-Test-Remote-IP", "192.168.1.10");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(lanRequest)).StatusCode);
        Assert.True(app.Services.GetRequiredService<SafeModeService>().Enabled);
        Assert.False(sessions.Validate(session));
    }

    [Fact]
    public async Task SecurityNotificationSettings_HaveDefaultsAndRejectEmptySpecificChannels()
    {
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP", "127.0.0.1");
        using var defaultsResponse = await client.GetAsync("/api/security/notifications");
        defaultsResponse.EnsureSuccessStatusCode();
        using var defaults = System.Text.Json.JsonDocument.Parse(await defaultsResponse.Content.ReadAsStringAsync());
        Assert.Equal("AllEnabled", defaults.RootElement.GetProperty("deliveryMode").GetString());
        Assert.Equal(SecurityEventDefaults.Notified.Select(type => type.ToString()), defaults.RootElement.GetProperty("events").EnumerateArray().Select(item => item.GetString()));

        var invalid = await client.PutAsJsonAsync("/api/security/notifications", new { events = new[] { "PasswordSet" }, deliveryMode = "SpecificChannels", channels = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var saved = await client.PutAsJsonAsync("/api/security/notifications", new { events = new[] { "PasswordSet", "LoginSourceLocked" }, deliveryMode = "SpecificChannels", channels = new[] { "telegram", "webhook" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
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
