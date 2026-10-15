using System.Collections.Concurrent;
using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Tasks;
using WinPilot.Features.Security;
using WinPilot.Features.Tasks;
using WinPilot.Features.Terminal;
using WinPilot.PluginContract;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class CancellationCallbackCleanupTest
{
    [Theory]
    [InlineData("update-disable", false)]
    [InlineData("update-disable", true)]
    [InlineData("rename", false)]
    [InlineData("rename", true)]
    [InlineData("disable", false)]
    [InlineData("disable", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    public async Task CompletedTaskMutationReturnsSuccessWhenCancellationCallbackThrows(string operation, bool throwCallback)
    {
        var logger = new CallbackLogger<TaskService>();
        await using var fixture = new TaskServiceFixture(logger: logger);
        using var scope = fixture.Extensions.CreateScope("cleanup-test", "Cleanup test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("测试单任务取消回调失败");
        var callbacks = 0;
        CancellationToken executionToken = default;
        ((IPluginTaskActionApi)scope).Register([new("block", "等待清理")], async (_, _, token) =>
        {
            executionToken = token;
            using var registration = token.Register(() =>
            {
                Interlocked.Increment(ref callbacks);
                if (throwCallback) throw failure;
            });
            started.TrySetResult();
            await release.Task;
            return PluginTaskActionResult.Success();
        });
        var task = fixture.Tasks.Create(new("original", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Extension, PluginId: "cleanup-test", ActionId: "block"),
             new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(fixture.Tasks);
        builder.Services.AddSingleton(fixture.Extensions);
        builder.Services.AddSingleton<TaskVariableCatalog>();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        await using var app = builder.Build();
        app.MapTaskEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        try
        {
            fixture.Tasks.RunNow(task.Name);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var next = operation == "rename" ? task with { Name = "renamed" } : task with { Enabled = false };
            using var response = operation switch
            {
                "delete" => await client.DeleteAsync("/api/tasks/original"),
                "disable" => await client.PatchAsJsonAsync("/api/tasks/original/enabled", new { enabled = false }),
                _ => await client.PutAsJsonAsync("/api/tasks/original", next)
            };
            Assert.Equal(operation == "delete" ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode);
            Assert.True(executionToken.IsCancellationRequested);
            Assert.Equal(1, callbacks);
            AssertWarning(logger.Entries, failure, throwCallback);
            if (throwCallback) Assert.Contains("original", Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Warning)).Message);
            var originalFile = Path.Combine(fixture.Paths.TasksDirectory, "original.json");
            var listed = await client.GetFromJsonAsync<JsonArray>("/api/tasks");
            if (operation == "delete")
            {
                Assert.Empty(fixture.Tasks.GetAll());
                Assert.Empty(listed!);
                Assert.False(File.Exists(originalFile));
                Assert.Equal("", await response.Content.ReadAsStringAsync());
            }
            else
            {
                var current = Assert.Single(fixture.Tasks.GetAll());
                Assert.Equal(next.Name, current.Name);
                Assert.Equal(next.Enabled, current.Enabled);
                var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Paths.TasksDirectory, next.Name + ".json")));
                var returned = JsonNode.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(JsonNode.DeepEquals(saved, returned));
                Assert.True(JsonNode.DeepEquals(saved, Assert.Single(listed!)));
                Assert.True(JsonNode.DeepEquals(saved, System.Text.Json.JsonSerializer.SerializeToNode(TaskPersistenceFile.From(current), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                })));
                if (operation == "rename") Assert.False(File.Exists(originalFile));
            }
        }
        finally
        {
            release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await fixture.Tasks.StopAsync(timeout.Token);
        }
        Assert.Equal(0, fixture.Programs.Launches);
        Assert.Throws<ObjectDisposedException>(() => executionToken.WaitHandle);
        AssertWarning(logger.Entries, failure, throwCallback);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SessionRevocationCompletesAndReleasesSourcesWhenCallbackThrows(bool dispose, bool throwCallback)
    {
        var logger = new CallbackLogger<SessionManager>();
        using var services = new ServiceCollection().AddSingleton<ILogger<SessionManager>>(logger).BuildServiceProvider();
        var sessions = ActivatorUtilities.CreateInstance<SessionManager>(services);
        var failure = new InvalidOperationException("测试会话取消回调失败");
        var tokens = new List<CancellationToken>();
        var ids = new List<string>();
        var registrations = new List<CancellationTokenRegistration>();
        var callbacks = 0;
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var id = sessions.Create();
                ids.Add(id);
                Assert.True(sessions.TryValidate(id, out var token));
                tokens.Add(token);
                registrations.Add(token.Register(() =>
                {
                    if (Interlocked.Increment(ref callbacks) == 1 && throwCallback) throw failure;
                }));
            }

            if (dispose) sessions.Dispose();
            else sessions.RevokeAll();

            Assert.Equal(3, callbacks);
            Assert.All(tokens, token =>
            {
                Assert.True(token.IsCancellationRequested);
                Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
            });
            Assert.All(ids, id => Assert.False(sessions.Validate(id)));
            AssertWarning(logger.Entries, failure, throwCallback);

            if (dispose)
            {
                sessions.Dispose();
                Assert.Throws<ObjectDisposedException>(() => sessions.Create());
            }
            else
            {
                sessions.RevokeAll();
                Assert.True(sessions.Validate(sessions.Create()));
            }
            Assert.Equal(3, callbacks);
            AssertWarning(logger.Entries, failure, throwCallback);
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
            sessions.RevokeAll();
            sessions.Dispose();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TaskShutdownContinuesThroughAllStagesWhenCallbackThrows(bool dispose, bool throwCallback)
    {
        var logger = new CallbackLogger<TaskService>();
        var serviceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.OnMessage = message => { if (message == "任务服务已启动") serviceStarted.TrySetResult(); };
        await using var fixture = new TaskServiceFixture(logger: logger);
        using var scope = fixture.Extensions.CreateScope("cleanup-test", "Cleanup test");
        var failure = new InvalidOperationException("测试任务取消回调失败");
        var started = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new ConcurrentDictionary<string, CancellationToken>();
        var registrations = new ConcurrentBag<CancellationTokenRegistration>();
        ((IPluginTaskActionApi)scope).Register([new("block", "等待清理")], async (_, context, token) =>
        {
            var taskName = Assert.IsType<string>(context.TaskName);
            tokens[taskName] = token;
            registrations.Add(token.Register(() =>
            {
                if (throwCallback && taskName == "Cleanup0") throw failure;
            }));
            started[int.Parse(taskName["Cleanup".Length..])].TrySetResult();
            await release.Task;
            return PluginTaskActionResult.Success();
        });
        for (var index = 0; index < 3; index++)
            fixture.Tasks.Create(new($"Cleanup{index}", true, new(TaskTriggerType.WinPilotStartup),
                [new(TaskActionType.Extension, PluginId: "cleanup-test", ActionId: "block")]));
        try
        {
            await fixture.Tasks.StartAsync(CancellationToken.None);
            await serviceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 3; index++) fixture.Tasks.RunNow($"Cleanup{index}");
            await Task.WhenAll(started.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            var listenerField = typeof(TaskService).GetField("processListenerStarted", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var pathsField = typeof(TaskService).GetField("processPaths", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var processPaths = (Dictionary<int, string>)pathsField.GetValue(fixture.Tasks)!;
            listenerField.SetValue(fixture.Tasks, true);
            processPaths[123456] = "cleanup-marker";

            if (dispose) fixture.Tasks.Dispose();
            else
            {
                var stopping = fixture.Tasks.StopAsync(CancellationToken.None);
                Assert.False(stopping.IsCompleted);
                release.TrySetResult();
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.Equal(3, tokens.Count);
            Assert.All(tokens.Values, token => Assert.True(token.IsCancellationRequested));
            Assert.False((bool)listenerField.GetValue(fixture.Tasks)!);
            Assert.Empty(processPaths);
            var serviceToken = (CancellationToken)typeof(TaskService).GetField("serviceToken", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Tasks)!;
            Assert.True(serviceToken.IsCancellationRequested);
            Assert.Contains(logger.Entries, entry => entry.Message == "任务触发所需的 Windows 进程监听已停止");
            AssertWarning(logger.Entries, failure, throwCallback);
            if (throwCallback) Assert.Contains("Cleanup0", Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Warning)).Message);
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
            release.TrySetResult();
            var running = (System.Collections.IDictionary)typeof(TaskService).GetField("runningTasks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Tasks)!;
            var gate = typeof(TaskService).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Tasks)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (gate) if (running.Count == 0) break;
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafeModePublishesStateAndClosesTerminalsWhenCallbackThrows(bool throwCallback)
    {
        await using var fixture = new TaskServiceFixture();
        var logger = new CallbackLogger<SafeModeService>();
        var safeMode = new SafeModeService(fixture.Paths, fixture.Terminals, logger);
        var sessions = (ConcurrentDictionary<Guid, TerminalSession>)typeof(TerminalSessionManager)
            .GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Terminals)!;
        var process = new TerminalProcess();
        var session = new TerminalSession(Guid.NewGuid(), Guid.NewGuid(), process, _ => Task.CompletedTask, item =>
        {
            sessions.TryRemove(item.Id, out _);
            return Task.CompletedTask;
        }, NullLogger.Instance);
        Assert.True(sessions.TryAdd(session.Id, session));
        using var watching = new CancellationTokenSource();
        await using var events = safeMode.WatchAsync(watching.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.False(events.Current);
        var changed = events.MoveNextAsync().AsTask();
        var failure = new InvalidOperationException("测试安全模式取消回调失败");
        var restricted = safeMode.RestrictedOperationsCancellation;
        using var callback = restricted.Register(() => { if (throwCallback) throw failure; });
        try
        {
            safeMode.SetEnabled(true);

            Assert.True(safeMode.Enabled);
            Assert.True(WinPilotSettings.LoadAndInitialize(fixture.Paths.ConfigurationFile).SafeModeEnabled);
            Assert.True(restricted.IsCancellationRequested);
            Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(events.Current);
            Assert.Empty(sessions);
            Assert.Equal(1, process.KillCount);
            Assert.Equal(1, process.DisposeCount);
            AssertWarning(logger.Entries, failure, throwCallback);
            safeMode.SetEnabled(true);
            Assert.Equal(1, process.DisposeCount);
            AssertWarning(logger.Entries, failure, throwCallback);
        }
        finally
        {
            watching.Cancel();
            try { await changed; } catch (OperationCanceledException) when (watching.IsCancellationRequested) { }
            ((CancellationTokenSource)typeof(SafeModeService).GetField("restrictedOperations", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(safeMode)!).Dispose();
        }
    }

    private static void AssertWarning(IEnumerable<(LogLevel Level, string Message, Exception? Error)> entries, Exception failure, bool expected)
    {
        var warnings = entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        if (!expected) { Assert.Empty(warnings); return; }
        var warning = Assert.Single(warnings);
        Assert.Same(failure, Assert.Single(Assert.IsType<AggregateException>(warning.Error).InnerExceptions));
    }

    private sealed class CallbackLogger<T> : ILogger<T>
    {
        internal ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();
        internal Action<string>? OnMessage { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Enqueue((level, message, exception));
            OnMessage?.Invoke(message);
        }
    }

    private sealed class TerminalProcess : ITerminalProcess
    {
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; } = new MemoryStream();
        public int KillCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int? ExitCode => KillCount == 0 ? null : 0;
        public bool HasExited() => KillCount != 0;
        public void Kill() => KillCount++;
        public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
        public void Resize(short columns, short rows) { }
        public void ClosePseudoConsoleHandle() { }
        public ValueTask CloseOutputAsync() => Output.DisposeAsync();
        public async ValueTask DisposeAsync() { DisposeCount++; await Input.DisposeAsync(); await Output.DisposeAsync(); }
    }
}
