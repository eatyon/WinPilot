using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinPilot.Core;
using WinPilot.Core.Plugins;
using WinPilot.Features.Plugins;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;
using static WinPilot.Test.PluginTestSupport;

namespace WinPilot.Test;

public sealed class PluginEndpointErrorTest
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClosedSessionSnapshotReturnsNotFoundWithoutInvokingPlugin(bool upload, bool capturedBeforeClose)
    {
        await using var host = await Host.CreateAsync();
        var handled = false;
        var id = host.Pages.Open("example", "page", session =>
        {
            session.RegisterMessageHandler((_, _) => { handled = true; return Task.FromResult("{}"); });
            session.RegisterUploadHandler((_, _) => { handled = true; return Task.FromResult("{}"); });
        });
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gate = typeof(PluginPageSessionManager).GetField("gate", flags)!.GetValue(host.Pages)!;
        var sessions = (System.Collections.IDictionary)typeof(PluginPageSessionManager).GetField("sessions", flags)!.GetValue(host.Pages)!;
        object snapshot;
        lock (gate) snapshot = sessions[id]!;
        await Task.Run(() => host.Pages.Close(id));
        if (capturedBeforeClose)
            lock (gate) sessions.Add(id, snapshot);
        try
        {
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, upload ? "application/octet-stream" : "application/json");
            using var response = await host.Client.PostAsync($"/api/plugin-page-sessions/{id}/{(upload ? "uploads?command=chunk" : "messages")}", content);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("插件页面会话不存在", problem.RootElement.GetProperty("detail").GetString());
            Assert.False(handled);
            Assert.Empty(host.Logs.Errors);
        }
        finally { host.Pages.Close(id); }
    }

    public static IEnumerable<object[]> CallbackFailures()
    {
        foreach (var operation in new[] { "action", "open", "message", "upload", "status" })
            foreach (var kind in new[] { "operation", "json", "key", "file", "unknown", "argument", "http" })
                yield return [operation, kind];
    }

    [Theory]
    [MemberData(nameof(CallbackFailures))]
    public async Task CallbackExceptionsAreServerProblemsWithoutLeakingDetailsOrPretendingSessionExpired(string operation, string kind)
    {
        await using var host = await Host.CreateAsync();
        var failure = CreateFailure(kind, host.Root + " private-secret");
        host.Runtime.Failure = failure;
        using var response = await host.RequestAsync(operation);
        await AssertServerProblem(response, host.Root);
        var log = Assert.Single(host.Logs.Errors);
        Assert.True(ContainsException(log, failure));
    }

    [Theory]
    [InlineData("GET", "{")]
    [InlineData("PUT", "{")]
    [InlineData("GET", "[]")]
    [InlineData("PUT", "[]")]
    [InlineData("GET", "null")]
    [InlineData("PUT", "null")]
    public async Task SavedConfigurationDamageIsNotReportedAsSubmittedInputError(string method, string content)
    {
        await using var host = await Host.CreateAsync();
        File.WriteAllText(host.ConfigurationFile, content);
        using var response = method == "GET"
            ? await host.Client.GetAsync("/api/plugins/example/configuration")
            : await host.Client.PutAsJsonAsync("/api/plugins/example/configuration", new { count = 3 });
        await AssertServerProblem(response, host.Root, "无法读取插件配置，请检查配置文件和程序日志");
        Assert.Equal(content, File.ReadAllText(host.ConfigurationFile));
        Assert.Equal(1, host.Runtime.Starts);
        Assert.Single(host.Logs.Errors);
    }

    [Theory]
    [InlineData("settings.json", "{")]
    [InlineData("settings.json", "{}")]
    [InlineData("features.json", "{")]
    [InlineData("features.json", "[]")]
    public async Task DamagedDeclarationsReturnFixedServerProblems(string file, string content)
    {
        await using var host = await Host.CreateAsync();
        File.WriteAllText(Path.Combine(host.PluginDirectory, file), content);
        using var response = await host.Client.GetAsync(file == "settings.json"
            ? "/api/plugins/example/configuration/schema" : "/api/plugins/example/features");
        await AssertServerProblem(response, host.Root);
        Assert.Single(host.Logs.Errors);
    }

    [Theory]
    [InlineData("{\"count\":null}", "数量不能为空")]
    [InlineData("{\"count\":\"bad\"}", "数量格式无效")]
    [InlineData("{\"count\":0}", "数量不能小于 1")]
    [InlineData("{\"count\":11}", "数量不能大于 9")]
    [InlineData("{\"count\":2}", "数量不符合步长 2")]
    [InlineData("[]", "插件配置格式无效，请重新打开配置")]
    public async Task SubmittedConfigurationValidationPreservesOriginalChineseError(string content, string detail)
    {
        await using var host = await Host.CreateAsync();
        using var request = new StringContent(content, System.Text.Encoding.UTF8, "application/json");
        using var response = await host.Client.PutAsync("/api/plugins/example/configuration", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(detail, problem.RootElement.GetProperty("detail").GetString());
        Assert.Empty(host.Logs.Errors);
        Assert.Equal(1, host.Runtime.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPageHandlersAreHostCapabilityFailures(bool upload)
    {
        await using var host = await Host.CreateAsync();
        var id = host.Pages.Open("example", "page", _ => { });
        using var request = new StringContent("{}", System.Text.Encoding.UTF8, upload ? "application/octet-stream" : "application/json");
        using var response = await host.Client.PostAsync($"/api/plugin-page-sessions/{id}/{(upload ? "uploads?command=chunk" : "messages")}", request);
        await AssertServerProblem(response, host.Root);
        Assert.Single(host.Logs.Errors);
    }

    [Fact]
    public async Task BusinessFailureResultRemainsSuccessfulHttpResponse()
    {
        await using var host = await Host.CreateAsync();
        host.Runtime.BusinessFailure = true;
        using var response = await host.RequestAsync("action");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(result.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal("业务未完成", result.RootElement.GetProperty("message").GetString());
        Assert.Empty(host.Logs.Errors);
    }

    [Theory]
    [InlineData("title", "首页标题不能超过 100 个字符")]
    [InlineData("source", "不支持的配置选项来源：unsupported")]
    [InlineData("mode", "该 Feature 当前不支持执行")]
    [InlineData("disabled", "插件未启用")]
    [InlineData("readme", "README.md 不能超过 512 KB")]
    [InlineData("collection", "配置组格式无效，请重新打开配置")]
    public async Task KnownHostRulesKeepTheirOriginalBadRequestDetails(string rule, string detail)
    {
        await using var host = await Host.CreateAsync();
        HttpResponseMessage response;
        switch (rule)
        {
            case "title": response = await host.Client.PutAsJsonAsync("/api/plugins/example/settings/display/status", new { homeTitle = new string('x', 101) }); break;
            case "source": response = await host.Client.GetAsync("/api/plugins/options/settings/unsupported"); break;
            case "mode": response = await host.Client.PostAsync("/api/plugins/example/features/page/execute", null); break;
            case "disabled":
                host.Manager.Disable("example");
                response = await host.Client.PostAsync("/api/plugins/example/features/page/page-sessions", null);
                break;
            case "readme":
                File.WriteAllText(Path.Combine(host.PluginDirectory, "README.md"), new string('x', 512 * 1024 + 1));
                response = await host.Client.GetAsync("/api/plugins/example/readme");
                break;
            default:
                File.WriteAllText(Path.Combine(host.PluginDirectory, "settings.json"), """[{"name":"groups","label":"配置组","type":"collection","mode":"dynamic","item":{"fields":[{"name":"path","type":"text"}]}}]""");
                response = await host.Client.PutAsJsonAsync("/api/plugins/example/configuration", new { groups = new { path = "x" } });
                break;
        }
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(detail, problem.RootElement.GetProperty("detail").GetString());
            Assert.Empty(host.Logs.Errors);
        }
    }

    [Fact]
    public async Task IncompatiblePluginEnableKeepsValidationMessageAndStateBehavior()
    {
        await using var host = await Host.CreateAsync();
        host.Manager.Disable("example");
        var manifestFile = Path.Combine(host.PluginDirectory, "manifest.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestFile))!.AsObject();
        manifest["winpilot_version"] = "999.0.0";
        File.WriteAllText(manifestFile, manifest.ToJsonString());
        host.Manager.Scan();
        host.Logs.Errors.Clear();
        using var response = await host.Client.PostAsync("/api/plugins/example/enable", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("需要 WinPilot 999.0.0 或更高版本", problem.RootElement.GetProperty("detail").GetString());
        Assert.Empty(host.Logs.Errors);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("upload")]
    public async Task CallbackCancellationPropagatesWithoutServerErrorLogging(string operation)
    {
        await using var host = await Host.CreateAsync();
        host.Runtime.Failure = new OperationCanceledException("controlled cancel");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RequestAsync(operation));
        Assert.Empty(host.Logs.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageWriteFailureReturnsServerProblemWithoutChangingFileOrRestarting(bool cleanupAlsoFails)
    {
        await using var host = await Host.CreateAsync();
        File.WriteAllText(host.ConfigurationFile, "{\"count\":1}");
        var original = File.ReadAllText(host.ConfigurationFile);
        var blocked = cleanupAlsoFails ? host.ConfigurationFile + ".tmp" : host.ConfigurationFile;
        if (cleanupAlsoFails) File.WriteAllText(blocked, "temporary");
        using (var locked = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var response = await host.Client.PutAsJsonAsync("/api/plugins/example/configuration", new { count = 3 });
            await AssertServerProblem(response, host.Root);
            Assert.Equal(original, File.ReadAllText(host.ConfigurationFile));
            Assert.Single(host.Logs.Errors);
            Assert.Equal(1, host.Runtime.Starts);
        }
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("settings")]
    [InlineData("features")]
    [InlineData("readme")]
    public async Task LockedStorageReadsReturnFixedServerProblems(string source)
    {
        await using var host = await Host.CreateAsync();
        var path = source switch
        {
            "configuration" => host.ConfigurationFile,
            "settings" => Path.Combine(host.PluginDirectory, "settings.json"),
            "features" => Path.Combine(host.PluginDirectory, "features.json"),
            _ => Path.Combine(host.PluginDirectory, "README.md")
        };
        if (!File.Exists(path)) File.WriteAllText(path, source == "configuration" ? "{\"count\":1}" : "说明");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var response = await host.Client.GetAsync(source switch
        {
            "configuration" => "/api/plugins/example/configuration",
            "settings" => "/api/plugins/example/configuration/schema",
            "features" => "/api/plugins/example/features",
            _ => "/api/plugins/example/readme"
        });
        await AssertServerProblem(response, host.Root);
        Assert.Single(host.Logs.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagedDisplaySettingsAreServerErrorsAndDoNotChangePluginState(bool save)
    {
        await using var host = await Host.CreateAsync();
        var file = Path.Combine(Path.GetDirectoryName(host.ConfigurationFile)!, "plugin.json");
        File.WriteAllText(file, "{ damaged }");
        var plugin = host.Manager.GetPlugin("example");
        using var response = save
            ? await host.Client.PutAsJsonAsync("/api/plugins/example/settings/display/configuration", new { registerOnHome = true })
            : await host.Client.GetAsync("/api/plugins/example/settings/display/configuration");
        await AssertServerProblem(response, host.Root);
        Assert.Single(host.Logs.Errors);
        Assert.Equal("{ damaged }", File.ReadAllText(file));
        Assert.Same(plugin, host.Manager.GetPlugin("example"));
    }

    private static async Task AssertServerProblem(HttpResponseMessage response, string root, string? expectedDetail = null)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(500, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("detail").GetString()));
        if (expectedDetail is not null) Assert.Equal(expectedDetail, problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(root, body);
        Assert.DoesNotContain("private-secret", body);
        Assert.DoesNotContain("Exception", body);
        Assert.False(problem.RootElement.TryGetProperty("stackTrace", out _));
    }

    private static Exception CreateFailure(string kind, string message) => kind switch
    {
        "operation" => new InvalidOperationException(message),
        "json" => new JsonException(message),
        "key" => new KeyNotFoundException(message),
        "file" => new FileNotFoundException(message),
        "argument" => new ArgumentException(message, "id"),
        "http" => new Microsoft.AspNetCore.Http.BadHttpRequestException(message, 401),
        _ => new Exception(message)
    };

    private static bool ContainsException(Exception exception, Exception expected) => ReferenceEquals(exception, expected)
        || exception is AggregateException aggregate && aggregate.InnerExceptions.Any(error => ContainsException(error, expected))
        || exception.InnerException is not null && ContainsException(exception.InnerException, expected);

    private sealed class Host : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "WinPilot.Plugin.Errors", Guid.NewGuid().ToString("N"));
        internal string PluginDirectory => Path.Combine(Root, "User", "Plugin", "example");
        internal string ConfigurationFile { get; private set; } = "";
        internal ErrorLogs Logs { get; } = new();
        internal Runtime Runtime { get; } = new();
        internal PluginPageSessionManager Pages { get; private set; } = null!;
        internal HttpClient Client { get; private set; } = null!;
        private PluginManager? manager;
        internal PluginManager Manager => manager!;
        private WebApplication? app;
        private ILoggerFactory? loggerFactory;
        private string sessionId = "";

        internal static async Task<Host> CreateAsync()
        {
            var host = new Host();
            try
            {
                var paths = PortableDataPaths.Create(host.Root);
                WritePlugin(paths, "example", "example", "");
                host.ConfigurationFile = Path.Combine(paths.PluginDataDirectory, "example", "config.json");
                File.WriteAllText(Path.Combine(host.PluginDirectory, "manifest.json"), """{"id":"example","name":"Example","version":"1.0.0","main":"plugin.dll","settings_ui":"settings.json","features_ui":"features.json"}""");
                File.WriteAllText(Path.Combine(host.PluginDirectory, "settings.json"), """[{"name":"count","label":"数量","type":"number","required":true,"default":1,"min":1,"max":9,"step":2}]""");
                File.WriteAllText(Path.Combine(host.PluginDirectory, "features.json"), """{"features":[{"id":"action","name":"Action","mode":"action"},{"id":"page","name":"Page","mode":"page","entry":"page.js"}]}""");
                Directory.CreateDirectory(Path.Combine(host.PluginDirectory, "web"));
                File.WriteAllText(Path.Combine(host.PluginDirectory, "web", "page.js"), "export default {};");
                host.loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(host.Logs));
                host.Pages = new(host.loggerFactory.CreateLogger<PluginPageSessionManager>());
                host.manager = CreatePluginManager(paths, host.Runtime, host.Pages, host.loggerFactory.CreateLogger<PluginManager>());
                host.sessionId = host.manager.OpenPageFeatureSession("example", "page").Id;
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseTestServer();
                builder.Logging.ClearProviders();
                builder.Logging.AddProvider(host.Logs);
                builder.Services.AddSingleton(paths);
                builder.Services.AddSingleton(host.manager);
                builder.Services.AddSingleton(new PluginWebResourceService(host.manager));
                builder.Services.AddSingleton<TerminalSessionManager>();
                builder.Services.AddSingleton<SafeModeService>();
                host.app = builder.Build();
                host.app.MapPluginEndpoints();
                await host.app.StartAsync();
                host.Client = host.app.GetTestClient();
                host.Logs.Errors.Clear();
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        internal Task<HttpResponseMessage> RequestAsync(string operation) => operation switch
        {
            "action" => Client.PostAsync("/api/plugins/example/features/action/execute", null),
            "open" => Client.PostAsync("/api/plugins/example/features/page/page-sessions", null),
            "status" => Client.GetAsync("/api/plugins/example/status"),
            "upload" => Client.PostAsync($"/api/plugin-page-sessions/{sessionId}/uploads?command=chunk", new ByteArrayContent([1]) { Headers = { ContentType = new("application/octet-stream") } }),
            _ => Client.PostAsync($"/api/plugin-page-sessions/{sessionId}/messages", new StringContent("{}"))
        };

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (app is not null) await app.DisposeAsync();
            Pages?.Dispose();
            manager?.Dispose();
            loggerFactory?.Dispose();
            TryDeleteDirectory(Root);
        }
    }

    private sealed class Runtime : IPluginRuntime, IPluginRuntimeInstance, IPluginRuntimeActionFeatures, IPluginRuntimePageFeatures, IPluginRuntimeStatus
    {
        internal Exception? Failure { get; set; }
        internal bool BusinessFailure { get; set; }
        internal int Starts { get; private set; }
        public IPluginRuntimeInstance Start(PluginInfo plugin) { Starts++; return this; }
        public string? StatusJson { get { Check(); return "{}"; } }
        public event Action<string>? StateChanged { add { } remove { } }
        public PluginFeatureExecutionResult ExecuteFeature(string featureId) { Check(); return new(!BusinessFailure, BusinessFailure ? "业务未完成" : "完成"); }
        public void OpenPage(string featureId, IPluginPageSession session)
        {
            Check();
            session.RegisterMessageHandler((_, _) => { Check(); return Task.FromResult("{}"); });
            session.RegisterUploadHandler((_, _) => { Check(); return Task.FromResult("{}"); });
        }
        private void Check() { if (Failure is not null) throw Failure; }
        public void Dispose() { }
    }

    private sealed class ErrorLogs : ILoggerProvider
    {
        internal ConcurrentQueue<Exception> Errors { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Errors);
        public void Dispose() { }
        private sealed class Logger(ConcurrentQueue<Exception> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { if (level == LogLevel.Error && exception is not null) errors.Enqueue(exception); }
        }
    }
}
