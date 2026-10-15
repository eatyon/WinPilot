using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Core;
using WinPilot.Features.Notifications;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class WebFallbackTest : IAsyncLifetime
{
    private const string IndexContent = "<html><body>routing-spa</body></html>";
    private const string ScriptContent = "console.log('routing-asset');";
    private const string Password = "routing test password";
    private readonly string root = Path.Combine(Path.GetTempPath(), "WinPilot.Web.Fallback.Test", Guid.NewGuid().ToString("N"));
    private WebApplication? app;
    private HttpClient client = null!;
    private int handledRequests;

    public async Task InitializeAsync()
    {
        try
        {
            var webRoot = Directory.CreateDirectory(Path.Combine(root, "wwwroot")).FullName;
            Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
            File.WriteAllText(Path.Combine(webRoot, "index.html"), IndexContent, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(webRoot, "assets", "app.js"), ScriptContent, new UTF8Encoding(false));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, WebRootPath = webRoot });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton(PortableDataPaths.Create(root));
            builder.Services.AddSingleton<AuthStore>();
            builder.Services.AddSingleton<SessionManager>();
            builder.Services.AddSingleton<LoginRateLimiter>();
            builder.Services.AddSingleton<INotificationService, TestNotificationService>();
            builder.Services.AddSingleton<SecurityEventNotifier>();
            builder.Services.AddSingleton<WebFacadeService>();
            builder.Services.AddSingleton<TerminalSessionManager>();
            builder.Services.AddSingleton<SafeModeService>();
            app = builder.Build();
            app.Services.GetRequiredService<AuthStore>().SetInitialPassword(Password);
            app.Services.GetRequiredService<SafeModeService>().SetEnabled(false);
            app.UseMiddleware<RequestCancellationMiddleware>();
            app.UseRouting();
            app.UseMiddleware<AuthenticationMiddleware>();
            app.UseMiddleware<SafeModeMiddleware>();
            app.UseDefaultFiles();
            var staticFileOptions = new StaticFileOptions
            {
                OnPrepareResponse = context =>
                {
                    if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
                        context.Context.Response.Headers.CacheControl = "no-cache";
                    else if (context.Context.Request.Path.StartsWithSegments("/assets"))
                        context.Context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                }
            };
            app.UseStaticFiles(staticFileOptions);
            app.MapAuthenticationEndpoints();
            app.MapGet("/api/single", ApiSuccess);
            app.MapMethods("/api/multiple", ["GET", "PUT", "DELETE"], ApiSuccess);
            app.MapMethods("/api/items/{id}", ["GET", "PATCH"], ApiSuccess);
            app.MapPost("/api/items/options", ApiSuccess);
            app.Map("/api/unrestricted", (HttpContext context) => ApiSuccess().ExecuteAsync(context));
            app.MapGet("/api/business-missing", (bool problem) => problem
                ? Results.Problem(statusCode: 404, title: "配置不存在", detail: "未找到请求的配置")
                : Results.NotFound(new { detail = "未找到请求的配置" }));
            app.MapGet("/api/files/protected", ApiSuccess);
            app.MapPost("/api/tasks", ApiSuccess);
            app.MapPut("/api/settings/protected", ApiSuccess);
            app.UseWebFallback(staticFileOptions);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StartAsync(timeout.Token);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.Add("Cookie", $"WinPilot.Session={app.Services.GetRequiredService<SessionManager>().Create()}");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    [Theory]
    [InlineData("/api/single", "POST", "GET")]
    [InlineData("/api/single", "HEAD", "GET")]
    [InlineData("/api/single", "OPTIONS", "GET")]
    [InlineData("/api/multiple", "POST", "DELETE,GET,PUT")]
    [InlineData("/api/multiple", "PATCH", "DELETE,GET,PUT")]
    [InlineData("/api/items/item", "DELETE", "GET,PATCH")]
    [InlineData("/API/items/item.json/", "POST", "GET,PATCH")]
    [InlineData("/api/items/options", "DELETE", "GET,PATCH,POST")]
    public async Task ExistingApiWithUnsupportedMethodReturns405AndOnlyRegisteredAllow(string path, string method, string allowed)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(allowed.Split(',').Order(), response.Content.Headers.Allow.Order());
        Assert.Equal(0, handledRequests);
        Assert.DoesNotContain("API 不存在", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task UnknownApiReturns404ProblemForEveryMethod(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/unknown");
        using var response = await client.SendAsync(request);
        await AssertApiNotFound(response, method == "HEAD");
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/")]
    [InlineData("/API")]
    [InlineData("/API/UNKNOWN")]
    [InlineData("/api/unknown/")]
    [InlineData("/api/unknown.json")]
    [InlineData("/api/unknown/child")]
    public async Task UnknownApiPathVariantsNeverBecomeSpa(string path)
    {
        using var response = await client.GetAsync(path);
        await AssertApiNotFound(response);
    }

    [Theory]
    [InlineData("/api/single", "GET")]
    [InlineData("/api/multiple", "GET")]
    [InlineData("/api/multiple", "PUT")]
    [InlineData("/api/multiple", "DELETE")]
    [InlineData("/api/items/item", "GET")]
    [InlineData("/api/items/item", "PATCH")]
    public async Task SupportedApiMethodStillExecutesOriginalEndpoint(string path, string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("api", json.RootElement.GetProperty("value").GetString());
        Assert.Equal(1, handledRequests);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("OPTIONS")]
    public async Task EndpointWithoutMethodRestrictionRemainsReachable(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/unrestricted");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handledRequests);
    }

    [Theory]
    [InlineData("/", "GET")]
    [InlineData("/features/deep/page", "GET")]
    [InlineData("/features/deep/page", "HEAD")]
    [InlineData("/login", "GET")]
    [InlineData("/login", "HEAD")]
    [InlineData("/apix/deep/page", "GET")]
    [InlineData("/api-extra/deep/page", "GET")]
    public async Task NonApiPageNavigationStillServesSpa(string path, string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Encoding.UTF8.GetByteCount(IndexContent), response.Content.Headers.ContentLength);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal(method == "HEAD" ? "" : IndexContent, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, handledRequests);
    }

    [Fact]
    public async Task LoginPostStillUsesAuthenticationEndpoint()
    {
        using var response = await client.PostAsJsonAsync("/login", new { password = Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("WinPilot.Session="));
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task StaticAssetRetainsContentAndCacheHeaders(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/assets/app.js");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Encoding.UTF8.GetByteCount(ScriptContent), response.Content.Headers.ContentLength);
        Assert.Equal(new[] { "immutable", "max-age=31536000", "private" },
            response.Headers.CacheControl!.ToString().Split(',').Select(value => value.Trim()).Order());
        Assert.Equal(method == "HEAD" ? "" : ScriptContent, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/assets/missing.js", "GET")]
    [InlineData("/assets/missing.js", "HEAD")]
    [InlineData("/missing.json", "GET")]
    public async Task MissingNonApiFileRemains404WithoutSpaOrApiProblem(string path, string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Business404RetainsOriginalResponse(bool problem)
    {
        using var response = await client.GetAsync($"/api/business-missing?problem={problem}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(problem ? "application/problem+json" : "application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("未找到请求的配置", json.RootElement.GetProperty("detail").GetString());
        if (problem) Assert.Equal("配置不存在", json.RootElement.GetProperty("title").GetString());
        else Assert.False(json.RootElement.TryGetProperty("title", out _));
        Assert.Empty(response.Content.Headers.Allow);
    }

    [Theory]
    [InlineData("/api/single", "GET", "application/json")]
    [InlineData("/api/single", "POST", "application/json")]
    [InlineData("/api/unknown", "GET", "application/json")]
    [InlineData("/api/unknown", "POST", "application/json")]
    [InlineData("/api/single", "GET", "text/html")]
    [InlineData("/api/single", "POST", "text/html")]
    [InlineData("/api/unknown", "GET", "text/html")]
    [InlineData("/api/unknown", "POST", "text/html")]
    [InlineData("/api/single", "GET", "text/html,application/xhtml+xml,*/*;q=0.8")]
    [InlineData("/api/unknown", "HEAD", "text/html,application/xhtml+xml,*/*;q=0.8")]
    public async Task AuthenticationRejectionRemains401BeforeFallback(string path, string method, string accept)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Accept.ParseAdd(accept);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(response.Content.Headers.Allow);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, handledRequests);
    }

    [Fact]
    public async Task UnauthenticatedPageNavigationStillRedirectsToLogin()
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/features/deep/page");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(IndexContent, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/files/protected", "GET")]
    [InlineData("/api/files/unknown", "GET")]
    [InlineData("/api/tasks", "POST")]
    [InlineData("/api/settings/protected", "PUT")]
    public async Task SafeModeRejectionRemains403BeforeFallback(string path, string method)
    {
        app!.Services.GetRequiredService<SafeModeService>().SetEnabled(true);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("safe_mode_restricted", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("安全模式下不可执行此操作", json.RootElement.GetProperty("message").GetString());
        Assert.False(json.RootElement.TryGetProperty("title", out _));
        Assert.Empty(response.Content.Headers.Allow);
        Assert.Equal(0, handledRequests);
    }

    private IResult ApiSuccess()
    {
        handledRequests++;
        return Results.Ok(new { value = "api" });
    }

    private static async Task AssertApiNotFound(HttpResponseMessage response, bool head = false)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(response.Content.Headers.Allow);
        var body = await response.Content.ReadAsStringAsync();
        if (head) Assert.Empty(body);
        else
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("API 不存在", json.RootElement.GetProperty("title").GetString());
        }
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        try
        {
            if (app is not null)
            {
                try { await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { await app.DisposeAsync(); }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
