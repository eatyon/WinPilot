using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    [Fact]
    public async Task TaskSaveResponsesMatchNormalizedPersistenceWithoutChangingStatusOrLocation()
    {
        await using var fixture = new TaskServiceFixture();
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        foreach (var create in new[] { true, false })
        {
            var task = new
            {
                name = "Normalized", enabled = false,
                trigger = new { type = "Process", @event = "Started", processPaths = new[] { $" {Environment.ProcessPath} ", Environment.ProcessPath!.ToUpperInvariant(), " " } },
                actions = new[] { new { type = "SendNotification", notificationTitle = (string?)null, notificationContent = "content", notificationChannel = "QQBot" } }
            };
            using var response = create ? await client.PostAsJsonAsync("/api/tasks", task) : await client.PutAsJsonAsync("/api/tasks/Normalized", task);
            Assert.Equal(create ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
            if (create) Assert.Equal("/api/tasks/Normalized", response.Headers.Location!.OriginalString);
            else Assert.Null(response.Headers.Location);
            var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Normalized.json")))!;
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(saved, json));
            Assert.Equal("Process", json["trigger"]!["type"]!.GetValue<string>());
            Assert.Equal("Started", json["trigger"]!["event"]!.GetValue<string>());
            Assert.False(json["trigger"]!.AsObject().ContainsKey("cron"));
            Assert.Equal(Environment.ProcessPath, Assert.Single(json["trigger"]!["processPaths"]!.AsArray())!.GetValue<string>());
            Assert.False(json["actions"]![0]!.AsObject().ContainsKey("notificationTitle"));
            Assert.Equal("content", json["actions"]![0]!["notificationContent"]!.GetValue<string>());
            Assert.Equal("QQBot", json["actions"]![0]!["notificationChannel"]!.GetValue<string>());
        }
        var original = File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Normalized.json"));
        using var rejected = await client.PutAsJsonAsync("/api/tasks/Normalized", new
        {
            name = "Normalized", enabled = false, trigger = new { type = "WinPilotStartup" }, actions = new[] { new { type = "Delay", delaySeconds = 0 } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(original, File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Normalized.json")));
        using var deleted = await client.DeleteAsync("/api/tasks/Normalized");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("", await deleted.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false, "healthy")]
    [InlineData(true, "healthy")]
    [InlineData(false, "other")]
    [InlineData(true, "other")]
    public async Task LoadingRejectsMismatchedTaskIdentityWithoutOverwritingOrRenamingFiles(bool writeAliasFirst, string aliasName)
    {
        string alias = "", healthy = "";
        await using var fixture = new TaskServiceFixture(initialize: paths =>
        {
            Directory.CreateDirectory(paths.TasksDirectory);
            string Content(string name, string program) => JsonSerializer.Serialize(TaskPersistenceFile.From(
                new(name, false, new(TaskTriggerType.WinPilotStartup), [new(TaskActionType.StartProgram, ProgramPath: program)])));
            alias = Content(aliasName, "alias.exe");
            healthy = Content("healthy", "app.exe");
            if (writeAliasFirst) File.WriteAllText(Path.Combine(paths.TasksDirectory, "alias.json"), alias);
            File.WriteAllText(Path.Combine(paths.TasksDirectory, "healthy.json"), healthy);
            if (!writeAliasFirst) File.WriteAllText(Path.Combine(paths.TasksDirectory, "alias.json"), alias);
        });
        var task = Assert.Single(fixture.Tasks.GetAll());
        Assert.Equal("healthy", task.Name);
        Assert.Equal("app.exe", Assert.Single(task.Actions).ProgramPath);
        Assert.IsType<InvalidDataException>(Assert.Single(fixture.Logger.Exceptions));
        Assert.Contains(fixture.Logger.Messages, message => message.Contains("alias.json", StringComparison.Ordinal));
        Assert.Equal(alias, File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "alias.json")));
        Assert.Equal(healthy, File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "healthy.json")));
        Assert.Equal(2, Directory.GetFiles(fixture.Paths.TasksDirectory).Length);
        fixture.Tasks.Delete("healthy");
        Assert.Empty(fixture.Tasks.GetAll());
        Assert.Equal(alias, File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "alias.json")));
    }

    [Fact]
    public async Task LoadingAcceptsCaseOnlyTaskNameDifferences()
    {
        await using var fixture = new TaskServiceFixture(initialize: paths =>
        {
            Directory.CreateDirectory(paths.TasksDirectory);
            var task = new WinPilotTask("NAME", false, new(TaskTriggerType.WinPilotStartup), [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]);
            File.WriteAllText(Path.Combine(paths.TasksDirectory, "Name.json"), JsonSerializer.Serialize(TaskPersistenceFile.From(task)));
        });
        Assert.Equal("NAME", Assert.Single(fixture.Tasks.GetAll()).Name);
        Assert.Empty(fixture.Logger.Exceptions);
        fixture.Tasks.SetEnabled("name", true);
        Assert.Single(Directory.GetFiles(fixture.Paths.TasksDirectory));
    }

    [Theory]
    [InlineData(false, "[]", true)]
    [InlineData(true, "[]", false)]
    [InlineData(false, "[\"one\"]", true)]
    [InlineData(true, "[\"one\"]", true)]
    [InlineData(false, "[\"missing\"]", false)]
    [InlineData(true, "[\"missing\"]", false)]
    [InlineData(false, "\"\"", false)]
    [InlineData(false, "{}", false)]
    [InlineData(false, "[\"\"]", false)]
    [InlineData(false, "[1]", false)]
    [InlineData(false, "[null]", false)]
    public async Task OptionalMultiCheckboxAllowsEmptyArrayAndStillValidatesSelectedOptions(bool required, string json, bool valid)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("choices", "Choices");
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, object?> { ["choices"] = document.RootElement.Clone() };
        var called = 0;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IPluginTaskActionApi)scope).Register([
            new("choose", "Choose", [new("choices", "Choices", PluginActionParameterType.MultiCheckbox,
                Required: required, Options: [new("one", "One")])])], (parameters, _, _) =>
            {
                Assert.Equal(json, ((JsonElement)parameters["choices"]!).GetRawText());
                called++;
                return Task.FromResult(PluginTaskActionResult.Success());
            });
        ((IPluginTaskActionApi)scope).Register([new("complete", "Complete")], (_, _, _) =>
        {
            complete.TrySetResult();
            return Task.FromResult(PluginTaskActionResult.Success());
        });
        fixture.Tasks.Create(new("Choices", true, new(TaskTriggerType.WinPilotStartup), [
            new(TaskActionType.Extension, PluginId: "choices", ActionId: "choose", Parameters: values, ContinueOnFailure: true),
            new(TaskActionType.Extension, PluginId: "choices", ActionId: "complete")]));
        fixture.Tasks.RunNow("Choices");
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(valid ? 1 : 0, called);
        if (valid) Assert.Empty(fixture.Logger.Exceptions);
        else Assert.IsType<InvalidOperationException>(Assert.Single(fixture.Logger.Exceptions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredTriggerCannotStartCurrentTaskEvenWithCurrentGenerationToken(bool useCurrentToken)
    {
        await using var fixture = new TaskServiceFixture();
        using var previous = fixture.Extensions.CreateScope("test", "Test");
        previous.Register([new("trigger", "Previous trigger")]);
        PluginTaskTriggerFired? received = null;
        fixture.Extensions.Triggered += fired => received = fired;
        var trigger = new PluginTriggerEvent("trigger", DateTimeOffset.UtcNow, new Dictionary<string, object?>());
        previous.Emit(trigger);
        Assert.NotNull(received);
        var expired = received;
        using var current = fixture.Extensions.CreateScope("test", "Test");
        previous.Dispose();
        current.Register([new("trigger", "Current trigger")]);
        current.Emit(trigger);
        Assert.NotNull(received);
        var currentToken = received.CancellationToken;
        fixture.Tasks.Create(new("current", true, new(TaskTriggerType.Extension, PluginId: "test", TriggerId: "trigger"),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        var replay = useCurrentToken ? expired with { CancellationToken = currentToken } : expired;
        typeof(TaskService).GetMethod("ExtensionTriggered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(fixture.Tasks, [replay]);
        Assert.Equal(0, fixture.Programs.Launches);
        current.Emit(trigger);
        Assert.Equal(1, fixture.Programs.Launches);
        Assert.Empty(fixture.Logger.Exceptions);
    }

    [Theory]
    [InlineData(TaskActionType.Keyboard, null, "发送按键「F11」")]
    [InlineData(TaskActionType.Keyboard, "Ctrl", "发送按键「Ctrl + F11」")]
    [InlineData(TaskActionType.SendNotification, "Windows", "发送「Windows 通知」通知")]
    [InlineData(TaskActionType.SendNotification, "QQBot", "发送「QQBot」通知")]
    [InlineData(TaskActionType.Extension, "test", "插件扩展「测试插件」")]
    [InlineData(TaskActionType.Extension, "missing", "插件扩展「missing」")]
    public async Task ActionLogsIncludeKeysChannelOrPluginName(TaskActionType type, string? value, string description)
    {
        await using var fixture = new TaskServiceFixture(new RecordingNotifications());
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        ((WinPilot.PluginContract.IPluginTaskActionApi)scope).Register([new("run", "测试动作")],
            (_, _, _) => Task.FromResult(WinPilot.PluginContract.PluginTaskActionResult.Success()));
        var action = type switch
        {
            TaskActionType.Keyboard => new TaskAction(type, Keys: value is null ? ["F11"] : [value, "F11"]),
            TaskActionType.SendNotification => new TaskAction(type, NotificationContent: "测试通知", NotificationChannel: value),
            _ => new TaskAction(type, PluginId: value, ActionId: "run", ContinueOnFailure: true)
        };
        fixture.Tasks.Create(new("test", true, new(TaskTriggerType.WinPilotStartup),
            [action, new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.RunNow("test");
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains($"任务「test」第 1 个动作开始：{description}", fixture.Logger.Messages);
        if (type == TaskActionType.Extension && value == "missing")
            Assert.Contains($"任务「test」第 1 个动作失败：{description}，继续执行后续动作", fixture.Logger.Messages);
    }


    [Fact]
    public async Task TaskExtensionApiReturnsRegisteredDefinitionsAndDynamicOptions()
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("example", "示例插件");
        scope.Register([new PluginTriggerDefinition("ready", "准备完成")]);
        ((IPluginTaskActionApi)scope).Register(
            [new PluginTaskActionDefinition("run", "运行动作", [new("target", "目标", PluginActionParameterType.Select,
                Options: [new("initial", "初始配置")])])],
            (_, _, _) => Task.FromResult(PluginTaskActionResult.Success()),
            (actionId, parameterKey, _) =>
            {
                Assert.Equal("run", actionId);
                Assert.Equal("target", parameterKey);
                return Task.FromResult<IReadOnlyList<PluginActionOption>>([new("saved", "已保存配置")]);
            });
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        using var triggers = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/extensions/triggers"));
        Assert.Equal("ready", Assert.Single(triggers.RootElement.EnumerateArray()).GetProperty("triggerId").GetString());
        using var actions = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/extensions/actions"));
        var action = Assert.Single(actions.RootElement.EnumerateArray());
        Assert.Equal("run", action.GetProperty("actionId").GetString());
        Assert.Equal("示例插件", action.GetProperty("pluginDisplayName").GetString());
        Assert.True(action.GetProperty("dynamicOptions").GetBoolean());
        var options = await client.GetFromJsonAsync<PluginActionOption[]>("/api/tasks/extensions/actions/example/run/options/target");
        Assert.Equal(new PluginActionOption("saved", "已保存配置"), Assert.Single(options!));
    }

    [Fact]
    public async Task TaskApiReturnsOnlyFieldsForTheSelectedActionType()
    {
        await using var fixture = new TaskServiceFixture();
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        var task = new
        {
            name = "Sparse", enabled = true, trigger = new { type = "WinPilotStartup" },
            actions = new[] { new { type = "StartProgram", programPath = Environment.ProcessPath! } }
        };
        using var created = await client.PostAsJsonAsync("/api/tasks", task);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/tasks/Sparse", created.Headers.Location!.OriginalString);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.False(createdJson.RootElement.GetProperty("actions")[0].TryGetProperty("delaySeconds", out _));
        using var listed = JsonDocument.Parse(await client.GetStringAsync("/api/tasks"));
        Assert.False(listed.RootElement[0].GetProperty("actions")[0].TryGetProperty("delaySeconds", out _));
        using var updated = await client.PutAsJsonAsync("/api/tasks/Sparse", new
        {
            task.name, task.enabled, task.trigger,
            actions = new[] { new { type = "Delay", delaySeconds = 5 } }
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        var action = updatedJson.RootElement.GetProperty("actions")[0];
        Assert.Equal(5, action.GetProperty("delaySeconds").GetInt32());
        Assert.False(action.TryGetProperty("programPath", out _));
        using var toggled = await client.PatchAsJsonAsync("/api/tasks/Sparse/enabled", new { enabled = false });
        using var toggledJson = JsonDocument.Parse(await toggled.Content.ReadAsStringAsync());
        Assert.False(toggledJson.RootElement.GetProperty("actions")[0].TryGetProperty("force", out _));
    }

    [Theory]
    [InlineData("POST", "trigger", "触发条件不能为空")]
    [InlineData("PUT", "trigger", "触发条件不能为空")]
    [InlineData("POST", "actions", "任务动作不能为空")]
    [InlineData("PUT", "actions", "任务动作不能为空")]
    [InlineData("POST", "actions[0]", "动作不能为空")]
    [InlineData("PUT", "actions[0]", "动作不能为空")]
    public async Task TaskApiRejectsNullStructureBeforeChangingTasks(string method, string field, string message)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Existing", enabled: false);
        var file = Path.Combine(fixture.Paths.TasksDirectory, "Existing.json");
        var original = File.ReadAllText(file);
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        var trigger = field == "trigger" ? "null" : """{"type":"WinPilotStartup"}""";
        var actions = field == "actions" ? "null" : field == "actions[0]" ? "[null]" : """[{"type":"Delay","delaySeconds":1}]""";
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? "/api/tasks" : "/api/tasks/Existing")
        {
            Content = new StringContent($$"""{"name":"Invalid","enabled":false,"trigger":{{trigger}},"actions":{{actions}}}""", System.Text.Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(message, Assert.Single(problem.RootElement.GetProperty("errors").GetProperty(field).EnumerateArray()).GetString());
        Assert.Equal("Existing", Assert.Single(fixture.Tasks.GetAll()).Name);
        Assert.Equal(original, File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(fixture.Paths.TasksDirectory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DelayMustBePositive(int seconds)
    {
        await using var fixture = new TaskServiceFixture();
        var error = Assert.Throws<TaskValidationException>(() => fixture.Tasks.Create(new(
            "Delay", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Delay, DelaySeconds: seconds)])));

        Assert.Equal("请输入有效的延迟秒数", error.Message);
        Assert.Equal("actions[0].delaySeconds", error.Field);
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("POST")]
    public async Task MissingTaskResourceReturns404WithoutChangingOtherTasks(string method)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Existing", enabled: false);
        var original = File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Existing.json"));
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        var route = "/api/tasks/Missing" + (method == "PATCH" ? "/enabled" : method == "POST" ? "/run" : "");
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "PUT") request.Content = JsonContent.Create(new WinPilotTask("Missing", false,
            new(TaskTriggerType.WinPilotStartup), [new(TaskActionType.Delay, DelaySeconds: 1)]));
        if (method == "PATCH") request.Content = JsonContent.Create(new { enabled = true });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("操作失败", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("任务不存在", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal("Existing", Assert.Single(fixture.Tasks.GetAll()).Name);
        Assert.Equal(original, File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Existing.json")));
        Assert.Single(Directory.GetFiles(fixture.Paths.TasksDirectory));
    }

    [Theory]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    public async Task DuplicateTaskNameReturns409AndPreservesTasksAndFiles(string method, bool fileOnly)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Original", enabled: false);
        if (fileOnly) File.WriteAllText(Path.Combine(fixture.Paths.TasksDirectory, "Existing.json"), "invalid task file");
        else fixture.Create("Existing", enabled: false);
        var files = Directory.GetFiles(fixture.Paths.TasksDirectory).ToDictionary(path => path, File.ReadAllText);
        var names = fixture.Tasks.GetAll().Select(task => task.Name).ToArray();
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? "/api/tasks" : "/api/tasks/Original")
        {
            Content = JsonContent.Create(new WinPilotTask("eXisting", false, new(TaskTriggerType.WinPilotStartup),
                [new(TaskActionType.Delay, DelaySeconds: 1)]))
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("操作失败", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(fileOnly ? "同名任务文件已存在" : "同名任务已存在", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(problem.RootElement.GetProperty("detail").GetString(),
            Assert.Single(problem.RootElement.GetProperty("errors").GetProperty("name").EnumerateArray()).GetString());
        Assert.Equal(names, fixture.Tasks.GetAll().Select(task => task.Name));
        Assert.Equal(files.Count, Directory.GetFiles(fixture.Paths.TasksDirectory).Length);
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllText(file.Key));
    }

    [Theory]
    [InlineData("POST", "name", "任务名称无效")]
    [InlineData("PUT", "name", "任务名称无效")]
    [InlineData("POST", "actions", "至少需要一个动作")]
    [InlineData("PUT", "actions", "至少需要一个动作")]
    [InlineData("POST", "delay", "请输入有效的延迟秒数")]
    [InlineData("PUT", "delay", "请输入有效的延迟秒数")]
    public async Task TaskValidationErrorsRemain400BeforeResourceConflictChecks(string method, string field, string message)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Existing", enabled: false);
        var original = File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Existing.json"));
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        var task = new WinPilotTask(field == "name" ? " " : "Existing", false, new(TaskTriggerType.WinPilotStartup),
            field == "actions" ? [] : [new(TaskActionType.Delay, DelaySeconds: field == "delay" ? 0 : 1)]);
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? "/api/tasks" : "/api/tasks/Existing")
        {
            Content = JsonContent.Create(task)
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
        var errorField = field == "delay" ? "actions[0].delaySeconds" : field;
        Assert.Equal(message, Assert.Single(problem.RootElement.GetProperty("errors").GetProperty(errorField).EnumerateArray()).GetString());
        Assert.Equal(original, File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Existing.json")));
        Assert.Single(fixture.Tasks.GetAll());
    }

    [Fact]
    public async Task ExtensionTaskCanBeSavedWithoutCurrentRegistrations()
    {
        await using var fixture = new TaskServiceFixture();
        var task = new WinPilotTask("UnavailableExtension", false,
            new(TaskTriggerType.Extension, PluginId: "missing", TriggerId: "event"),
            [new(TaskActionType.Extension, PluginId: "missing", ActionId: "run")]);
        var saved = fixture.Tasks.Create(task);
        var updated = fixture.Tasks.Update(task.Name, task);
        foreach (var result in new[] { saved, updated })
        {
            Assert.Equal("missing", result.Trigger.PluginId);
            Assert.Equal("event", result.Trigger.TriggerId);
            Assert.Equal("missing", Assert.Single(result.Actions).PluginId);
            Assert.Equal("run", Assert.Single(result.Actions).ActionId);
        }
        Assert.Single(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData("name", "name", "任务名称无效")]
    [InlineData("cron", "trigger.cron", "Cron 表达式格式无效")]
    [InlineData("paths", "trigger.processPaths", "程序路径必须是绝对路径")]
    [InlineData("event", "trigger.event", "请选择事件")]
    [InlineData("event-invalid", "trigger.event", "程序事件类型无效")]
    [InlineData("program", "actions[0].programPath", "请选择要启动的程序")]
    [InlineData("program-relative", "actions[0].programPath", "程序路径必须是绝对路径")]
    [InlineData("program-type", "actions[0].programPath", "不支持此文件类型")]
    [InlineData("program-missing", "actions[0].programPath", "找不到指定的程序文件")]
    [InlineData("working-directory", "actions[0].workingDirectory", "起始目录不存在")]
    [InlineData("working-directory-file", "actions[0].workingDirectory", "起始目录不存在")]
    [InlineData("tag", "actions[0].tagName", "请选择快捷标签")]
    [InlineData("process", "actions[0].processName", "请输入进程名")]
    [InlineData("keyboard", "actions[0].keys", "请选择至少一个按键")]
    [InlineData("keyboard-count", "actions[0].keys", "最多选择 3 个按键")]
    [InlineData("keyboard-duplicate", "actions[0].keys", "按键不能重复")]
    [InlineData("keyboard-invalid", "actions[0].keys", "包含不支持的按键")]
    [InlineData("extension-trigger-plugin", "trigger.pluginId", "请选择插件和触发条件")]
    [InlineData("extension-trigger-id", "trigger.triggerId", "请选择插件和触发条件")]
    [InlineData("extension-action-plugin", "actions[0].pluginId", "请选择插件和动作")]
    [InlineData("extension-action-id", "actions[0].actionId", "请选择插件和动作")]
    [InlineData("shell", "actions[0].shell", "Shell 仅支持 CMD 或 PowerShell")]
    [InlineData("command", "actions[0].command", "请输入 Shell 命令")]
    [InlineData("notification", "actions[0].notificationContent", "请输入通知内容")]
    [InlineData("image", "actions[0].notificationContent", "图片仅允许一个")]
    public async Task TaskSaveValidationReturnsFieldPathsWithoutChangingFiles(string kind, string field, string message)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Original", enabled: false);
        var originalFile = Path.Combine(fixture.Paths.TasksDirectory, "Original.json");
        var originalContent = File.ReadAllText(originalFile);
        var task = new WinPilotTask("Target", false, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Delay, DelaySeconds: 1)]);
        task = kind switch
        {
            "name" => task with { Name = "invalid?" },
            "cron" => task with { Trigger = new(TaskTriggerType.Cron, Cron: "invalid") },
            "paths" => task with { Trigger = new(TaskTriggerType.Process, ProcessPaths: ["relative.exe"], Event: TaskProcessEventType.Started) },
            "event" => task with { Trigger = new(TaskTriggerType.Process, ProcessPaths: ["C:\\app.exe"]) },
            "event-invalid" => task with { Trigger = new(TaskTriggerType.Process, ProcessPaths: ["C:\\app.exe"], Event: (TaskProcessEventType)999) },
            "program" => task with { Actions = [new(TaskActionType.StartProgram)] },
            "program-relative" => task with { Actions = [new(TaskActionType.StartProgram, ProgramPath: "app.exe")] },
            "program-type" => task with { Actions = [new(TaskActionType.StartProgram, ProgramPath: Path.Combine(fixture.Paths.ApplicationDirectory, "app.txt"))] },
            "program-missing" => task with { Actions = [new(TaskActionType.StartProgram, ProgramPath: Path.Combine(fixture.Paths.ApplicationDirectory, "missing.exe"))] },
            "working-directory" => task with { Actions = [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!, WorkingDirectory: Path.Combine(fixture.Paths.ApplicationDirectory, "missing"))] },
            "working-directory-file" => task with { Actions = [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!, WorkingDirectory: Environment.ProcessPath!)] },
            "tag" => task with { Actions = [new(TaskActionType.StartTag)] },
            "process" => task with { Actions = [new(TaskActionType.CloseProcess)] },
            "keyboard" => task with { Actions = [new(TaskActionType.Keyboard)] },
            "keyboard-count" => task with { Actions = [new(TaskActionType.Keyboard, Keys: ["A", "B", "C", "D"])] },
            "keyboard-duplicate" => task with { Actions = [new(TaskActionType.Keyboard, Keys: ["A", "a"])] },
            "keyboard-invalid" => task with { Actions = [new(TaskActionType.Keyboard, Keys: ["NoSuchKey"])] },
            "extension-trigger-plugin" => task with { Trigger = new(TaskTriggerType.Extension, PluginId: " ", TriggerId: "event") },
            "extension-trigger-id" => task with { Trigger = new(TaskTriggerType.Extension, PluginId: "plugin") },
            "extension-action-plugin" => task with { Actions = [new(TaskActionType.Extension, PluginId: " ", ActionId: "run")] },
            "extension-action-id" => task with { Actions = [new(TaskActionType.Extension, PluginId: "plugin")] },
            "shell" => task with { Actions = [new(TaskActionType.Shell, Shell: "unsupported", Command: "echo test")] },
            "command" => task with { Actions = [new(TaskActionType.Shell, Shell: "CMD")] },
            "notification" => task with { Actions = [new(TaskActionType.SendNotification, NotificationChannel: "QQBot")] },
            "image" => task with { Actions = [new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&screenshot\n&screenshot")] },
            _ => task
        };
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? "/api/tasks" : "/api/tasks/Original")
            { Content = JsonContent.Create(task) };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(message, Assert.Single(problem.RootElement.GetProperty("errors").GetProperty(field).EnumerateArray()).GetString());
            Assert.Equal(originalContent, File.ReadAllText(originalFile));
            Assert.Single(Directory.GetFiles(fixture.Paths.TasksDirectory));
            Assert.Single(fixture.Tasks.GetAll());
        }
    }

    [Fact]
    public async Task TaskResourceSuccessResponsesPreserve201200204AndLocation()
    {
        await using var fixture = new TaskServiceFixture();
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        var task = new WinPilotTask("Original", false, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]);
        using var created = await client.PostAsJsonAsync("/api/tasks", task);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/tasks/Original", created.Headers.Location?.OriginalString);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("Original", createdJson.RootElement.GetProperty("name").GetString());
        var renamed = task with { Name = "Renamed" };
        using var updated = await client.PutAsJsonAsync("/api/tasks/Original", renamed);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal("Renamed", updatedJson.RootElement.GetProperty("name").GetString());
        using var enabled = await client.PatchAsJsonAsync("/api/tasks/Renamed/enabled", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using var enabledJson = JsonDocument.Parse(await enabled.Content.ReadAsStringAsync());
        Assert.True(enabledJson.RootElement.GetProperty("enabled").GetBoolean());
        using var running = await client.PostAsync("/api/tasks/Renamed/run", null);
        Assert.Equal(HttpStatusCode.NoContent, running.StatusCode);
        Assert.Empty(await running.Content.ReadAsByteArrayAsync());
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deleted = await client.DeleteAsync("/api/tasks/Renamed");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(86400, true)]
    [InlineData(86401, false)]
    [InlineData(int.MaxValue, false)]
    public async Task DelayCannotExceedOneDay(int seconds, bool valid)
    {
        await using var fixture = new TaskServiceFixture();
        var task = new WinPilotTask("Delay", false, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Delay, DelaySeconds: seconds)]);
        if (valid)
        {
            fixture.Tasks.Create(task);
            Assert.Equal(seconds, Assert.Single(fixture.Tasks.GetAll()).Actions[0].DelaySeconds);
        }
        else
        {
            var error = Assert.Throws<TaskValidationException>(() => fixture.Tasks.Create(task));
            Assert.Equal("延迟时间不能超过 86400 秒（24 小时）", error.Message);
            Assert.Equal("actions[0].delaySeconds", error.Field);
            Assert.Empty(fixture.Tasks.GetAll());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseProcess_CurrentUserOnlyFiltersOwnerAndProcessName(bool force)
    {
        await using var fixture = new TaskServiceFixture();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        fixture.Programs.RunningPrograms = [
            new(101, "APP.exe", identity.Name, 1),
            new(102, "app.exe", "OtherUser", 2),
            new(103, "other.exe", identity.Name, 1)
        ];
        fixture.Tasks.Create(new("Close", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.CloseProcess, ProcessName: "app.exe", Force: force, CurrentUserOnly: true)]));

        fixture.Tasks.RunNow("Close");
        var closed = await fixture.Programs.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(identity.Name, fixture.Programs.QueriedUserName);
        Assert.Equal(new[] { 101 }, closed.Ids);
        Assert.Equal(force, closed.Force);
    }

    [Fact]
    public async Task RunningTaskCannotReenterAndCanRunAgainAfterCompletion()
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        ((WinPilot.PluginContract.IPluginTaskActionApi)scope).Register([new("block", "Block")], async (_, _, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return WinPilot.PluginContract.PluginTaskActionResult.Success();
        });
        fixture.Tasks.Create(new("Example", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Extension, PluginId: "test", ActionId: "block"), new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.RunNow("Example");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Tasks.RunNow("Example");
        Assert.Equal(1, calls);
        release.SetResult();
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref fixture.Programs.Launches) < 2)
        {
            timeout.Token.ThrowIfCancellationRequested();
            fixture.Tasks.RunNow("Example");
            if (Volatile.Read(ref fixture.Programs.Launches) < 2) await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrDisposeCancelsRunsAndPreventsLaterSynchronousActions(bool dispose)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        ((WinPilot.PluginContract.IPluginTaskActionApi)scope).Register([new("block", "Block")], async (_, _, token) =>
        {
            operationToken = token;
            started.SetResult();
            await release.Task;
            return WinPilot.PluginContract.PluginTaskActionResult.Success();
        });
        fixture.Tasks.Create(new("Example", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Extension, PluginId: "test", ActionId: "block"), new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.RunNow("Example");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? stopping = null;
        if (dispose) fixture.Tasks.Dispose();
        else { stopping = fixture.Tasks.StopAsync(CancellationToken.None); Assert.False(stopping.IsCompleted); }
        Assert.True(operationToken.IsCancellationRequested);
        fixture.Logger.ActionCancelled = true;
        release.SetResult();
        if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Logger.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Tasks.RunNow("Example");
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rename")]
    public async Task ExistingUnloadedFilePreventsCreateAndRename(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        var original = Assert.Single(fixture.Tasks.GetAll());
        var originalFile = Path.Combine(fixture.Paths.TasksDirectory, "Example.json");
        var originalContent = File.ReadAllText(originalFile);
        var target = Path.Combine(fixture.Paths.TasksDirectory, "Unloaded.json");
        const string invalidContent = "invalid task";
        File.WriteAllText(target, invalidContent);
        var replacement = original with { Name = "Unloaded" };

        var exception = Assert.Throws<WinPilot.Core.ResourceNameConflictException>(() =>
        {
            if (operation == "create") fixture.Tasks.Create(replacement);
            else fixture.Tasks.Update(original.Name, replacement);
        });

        Assert.Equal("同名任务文件已存在", exception.Message);
        Assert.Equal(original, Assert.Single(fixture.Tasks.GetAll()));
        Assert.Equal(originalContent, File.ReadAllText(originalFile));
        Assert.Equal(invalidContent, File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(fixture.Paths.TasksDirectory, "*.tmp"));
    }

    [Fact]
    public async Task FailedRenameSerializationRestoresOriginalFileAndMemory()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        var original = Assert.Single(fixture.Tasks.GetAll());
        var file = Path.Combine(fixture.Paths.TasksDirectory, "Example.json");
        var content = File.ReadAllText(file);
        var parameters = new Dictionary<string, object?>();
        parameters["cycle"] = parameters;
        var replacement = original with
        {
            Name = "Renamed",
            Trigger = new(TaskTriggerType.Extension, PluginId: "test", TriggerId: "trigger", Parameters: parameters)
        };

        Assert.Throws<System.Text.Json.JsonException>(() => fixture.Tasks.Update(original.Name, replacement));
        Assert.Equal(original, Assert.Single(fixture.Tasks.GetAll()));
        Assert.Equal(content, File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.TasksDirectory, "Renamed.json")));
        Assert.Empty(Directory.GetFiles(fixture.Paths.TasksDirectory, "*.tmp"));
    }

    [Fact]
    public async Task FailedRenameAndRollbackKeepBothFailuresAndDoNotUpdateMemory()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        var original = Assert.Single(fixture.Tasks.GetAll());
        var file = Path.Combine(fixture.Paths.TasksDirectory, "Example.json");
        var renamed = Path.Combine(fixture.Paths.TasksDirectory, "Renamed.json");
        var content = File.ReadAllText(file);
        var failure = new IOException("受控保存失败");
        var value = new FailingPersistenceValue(() =>
        {
            File.WriteAllText(file, "external");
            throw failure;
        });
        var replacement = original with
        {
            Name = "Renamed",
            Trigger = new(TaskTriggerType.Extension, PluginId: "test", TriggerId: "trigger",
                Parameters: new Dictionary<string, object?> { ["value"] = value })
        };

        var error = Assert.Throws<AggregateException>(() => fixture.Tasks.Update(original.Name, replacement));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Same(failure, error.InnerExceptions[0]);
        Assert.IsAssignableFrom<IOException>(error.InnerExceptions[1]);
        Assert.Equal(original, Assert.Single(fixture.Tasks.GetAll()));
        Assert.Equal(content, File.ReadAllText(renamed));
        Assert.Equal("external", File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(fixture.Paths.TasksDirectory, "*.tmp"));
    }

    private sealed class FailingPersistenceValue(Action fail)
    {
        public int Value { get { fail(); return 0; } }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("rename")]
    public async Task FailedPersistencePreservesTaskAndRemovesTemporaryFiles(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        var original = Assert.Single(fixture.Tasks.GetAll());
        var file = Path.Combine(fixture.Paths.TasksDirectory, "Example.json");
        var content = File.ReadAllText(file);
        var target = Path.Combine(fixture.Paths.TasksDirectory, "Blocked.json");
        Directory.CreateDirectory(target);
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var exception = Record.Exception(() =>
            {
                switch (operation)
                {
                    case "create": fixture.Tasks.Create(original with { Name = "Blocked" }); break;
                    case "update": fixture.Tasks.Update(original.Name, original with { Enabled = false }); break;
                    case "disable": fixture.Tasks.SetEnabled(original.Name, false); break;
                    case "delete": fixture.Tasks.Delete(original.Name); break;
                    case "rename": fixture.Tasks.Update(original.Name, original with { Name = "Blocked" }); break;
                }
            });
            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.Equal(original, Assert.Single(fixture.Tasks.GetAll()));
        }
        Assert.Equal(content, File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(fixture.Paths.TasksDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("stop")]
    public async Task ChangingTaskLifecycleCancelsRunningActionAndSkipsRemainingActions(string operation)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("test", "Test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((WinPilot.PluginContract.IPluginTaskActionApi)scope).Register([new("block", "Block")], async (_, _, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                fixture.Logger.ActionCancelled = true;
                throw;
            }
            return WinPilot.PluginContract.PluginTaskActionResult.Success();
        });
        var task = new WinPilotTask("Example", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.Extension, PluginId: "test", ActionId: "block"),
             new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]);
        fixture.Tasks.Create(task);
        fixture.Tasks.RunNow(task.Name);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (operation)
        {
            case "delete": fixture.Tasks.Delete(task.Name); break;
            case "rename": fixture.Tasks.Update(task.Name, task with { Name = "Renamed" }); break;
            case "stop": await fixture.Tasks.StopAsync(CancellationToken.None); break;
        }
        await fixture.Logger.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Programs.Launches);
        if (operation == "delete") Assert.Empty(fixture.Tasks.GetAll());
        if (operation == "rename") Assert.Equal("Renamed", Assert.Single(fixture.Tasks.GetAll()).Name);
    }

    [Theory]
    [InlineData("app", "C:\\tools\\app.exe", true)]
    [InlineData("APP", "c:\\TOOLS\\APP.exe", true)]
    [InlineData("app", "C:\\other\\app.exe", false)]
    [InlineData("other", "C:\\tools\\app.exe", false)]
    [InlineData("app", "", false)]
    public void ProcessTriggerRequiresBothNameAndConfiguredPath(string name, string path, bool matches)
        => Assert.Equal(matches, TaskService.Matches(["C:\\tools\\app.exe"], name, path));

    [Theory]
    [InlineData(TaskTriggerType.Cron, "invalid", "")]
    [InlineData(TaskTriggerType.Cron, "", "")]
    [InlineData(TaskTriggerType.Cron, "0 8 * * *", "")]
    [InlineData(TaskTriggerType.Cron, "0 0 8 * * * 2030", "")]
    [InlineData(TaskTriggerType.Cron, "60 * * * * *", "")]
    [InlineData(TaskTriggerType.Process, "", "relative.exe")]
    [InlineData(TaskTriggerType.Process, "", "")]
    public async Task InvalidAutomaticTriggersAreRejectedWithoutSaving(TaskTriggerType type, string cron, string processPath)
    {
        await using var fixture = new TaskServiceFixture();
        var error = Assert.Throws<TaskValidationException>(() => fixture.Tasks.Create(new("invalid", true,
            new(type, Cron: cron, ProcessPaths: string.IsNullOrEmpty(processPath) ? [] : [processPath], Event: type == TaskTriggerType.Process ? TaskProcessEventType.Started : null),
            [new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)])));
        Assert.Equal(type == TaskTriggerType.Cron ? "trigger.cron" : "trigger.processPaths", error.Field);
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Fact]
    public async Task AutomaticStartupStillSkipsDisabledTasksWhileManualRunAllowsThem()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("已关闭", false);
        fixture.Create("已开启");
        fixture.Tasks.NotifyPluginsLoaded();
        await fixture.Tasks.StartAsync(CancellationToken.None);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Programs.Launches);
        fixture.Tasks.RunNow("已关闭");
        Assert.Equal(2, fixture.Programs.Launches);
        Assert.False(fixture.Tasks.GetAll().Single(task => task.Name == "已关闭").Enabled);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("QQBot")]
    public async Task NotificationActionUsesSelectedChannelAndPersistsIt(string? channel)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        fixture.Tasks.Create(new("Notification", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.SendNotification, NotificationContent: "测试通知", NotificationChannel: channel)]));
        Assert.Contains("测试通知", File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Notification.json")));
        var saved = Assert.Single(fixture.Tasks.GetAll()).Actions.Single();
        Assert.Equal(TaskActionType.SendNotification, saved.Type);
        Assert.Equal(channel ?? "", saved.NotificationChannel);
        var persisted = TaskActionPersistence.From(saved);
        Assert.Equal(string.IsNullOrWhiteSpace(channel) ? null : channel, persisted.NotificationChannel);
        var restored = persisted.ToAction();
        Assert.Equal(TaskActionType.SendNotification, restored.Type);
        Assert.Equal(persisted.NotificationChannel, restored.NotificationChannel);
        fixture.Tasks.RunNow("Notification");
        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("测试通知", sent.Message.Content);
        Assert.Equal(saved.NotificationChannel, sent.Channel);
    }

    [Theory]
    [InlineData("测试通知", true)]
    [InlineData("", false)]
    public async Task NotificationEmptyImageVariableIsTreatedAsNoImage(string content, bool succeeds)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([], "image/png"));

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot",
            NotificationContent: string.IsNullOrEmpty(content) ? "&test.image" : $"{content}\n&test.image"), succeeds);

        if (succeeds)
        {
            var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(content, sent.Message.Content);
            Assert.Null(sent.Message.ImagePath);
            Assert.Null(notifications.ImageBytes);
        }
        else
        {
            Assert.False(notifications.Sent.Task.IsCompleted);
        }
    }

    [Theory]
    [InlineData(1, "image/png", ".png")]
    [InlineData(10 * 1024 * 1024, "image/png", ".png")]
    [InlineData(1, "IMAGE/JPEG", ".jpg")]
    [InlineData(10 * 1024 * 1024, "image/jpeg", ".jpg")]
    public async Task NotificationImageVariableAcceptsSizeBoundariesAndDeclaredFormat(int length, string contentType, string extension)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        var image = new byte[length];
        using var scope = RegisterNotificationImage(fixture, new(image, contentType));

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("", sent.Message.Content);
        Assert.Equal(extension, Path.GetExtension(sent.Message.ImagePath));
        Assert.Equal(image, notifications.ImageBytes);
        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Theory]
    [InlineData(10 * 1024 * 1024 + 1, "image/png", "通知图片不能超过 10 MB")]
    [InlineData(1, "image/gif", "通知图片仅支持 PNG 或 JPEG")]
    [InlineData(1, "image/webp", "通知图片仅支持 PNG 或 JPEG")]
    [InlineData(1, "image/jpg", "通知图片仅支持 PNG 或 JPEG")]
    public async Task NotificationImageVariableRejectsInvalidSizeOrFormat(int length, string contentType, string expectedError)
    {
        var image = new PluginImageContent(new byte[length], contentType);
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, image);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"), false);

        var error = Assert.IsType<InvalidOperationException>(Assert.Single(fixture.Logger.Exceptions));
        Assert.Equal(expectedError, error.Message);
        Assert.False(notifications.Sent.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationFailedResultsFailActionAndRemoveTemporaryImage(bool partialFailure)
    {
        var notifications = new RecordingNotifications(_ => Task.FromResult<IReadOnlyList<NotificationChannelResult>>(partialFailure
            ? [new("Windows", true), new("QQBot", false), new("qqbot", false)]
            : []));
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([137, 80, 78, 71], "image/png"));

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"), false);

        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Fact]
    public async Task NotificationDeliveryExceptionFailsActionAndRemovesTemporaryImage()
    {
        var notifications = new RecordingNotifications(_ => Task.FromException<IReadOnlyList<NotificationChannelResult>>(new IOException("测试投递异常")));
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([137, 80, 78, 71], "image/png"));

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"), false);

        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationCancellationRemovesTemporaryImageAndSkipsRemainingActions(bool duringDelivery)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new RecordingNotifications(async token =>
        {
            if (duringDelivery)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            else
                await release.Task;
            return [new("Test", true)];
        });
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = RegisterNotificationImage(fixture, new([137, 80, 78, 71], "image/png"));
        StartNotification(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: "&test.image"));
        await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await CancelNotificationAsync(fixture, notifications, () => release.TrySetResult());

        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task NotificationLocalImagePathRemainsUnchangedAfterDelivery(string outcome)
    {
        var notifications = new RecordingNotifications(async token =>
        {
            if (outcome == "cancel")
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [new("Test", outcome != "failure")];
        });
        await using var fixture = new TaskServiceFixture(notifications);
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        var path = Path.Combine(fixture.Paths.ApplicationDirectory, "通知图片.png");
        var image = new byte[] { 137, 80, 78, 71 };
        await File.WriteAllBytesAsync(path, image);
        var action = new TaskAction(TaskActionType.SendNotification, NotificationChannel: "QQBot", NotificationContent: $"&image=\"{path}\"");
        if (outcome == "cancel")
        {
            StartNotification(fixture, action);
            await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await CancelNotificationAsync(fixture, notifications);
        }
        else
        {
            await RunNotificationAsync(fixture, action, outcome == "success");
        }

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(path, sent.Message.ImagePath);
        Assert.Equal("", sent.Message.Content);
        Assert.Equal(image, notifications.ImageBytes);
        Assert.True(File.Exists(path));
        Assert.Equal(image, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(" telegram ")]
    [InlineData(" QQBot ")]
    public async Task NotificationChannelWhitespaceIsPassedWithoutNormalization(string channel)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification,
            NotificationContent: "测试通知", NotificationChannel: channel), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(channel, sent.Channel);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("任务标题", "任务标题")]
    public async Task NotificationUsesConfiguredTaskTitle(string? title, string expectedTitle)
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot",
            NotificationTitle: title, NotificationContent: "测试通知"), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedTitle, sent.Message.Title);
    }

    [Fact]
    public async Task NotificationResolvesRequestedVariablesAndSendTimeButPreservesTitle()
    {
        var notifications = new RecordingNotifications();
        await using var fixture = new TaskServiceFixture(notifications);
        using var scope = fixture.Extensions.CreateScope("test", "测试插件");
        IReadOnlyList<string>? requested = null;
        var calls = 0;
        var image = new byte[] { 137, 80, 78, 71 };
        ((IPluginNotificationVariableApi)scope).Register([
            new("$test.summary", "结果"), new("&test.image", "图片", Type: PluginVariableType.Image)
        ], (names, _, _) =>
        {
            calls++;
            requested = names;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["$test.summary"] = "已完成", ["&test.image"] = new PluginImageContent(image, "image/png")
            });
        });
        var before = DateTimeOffset.Now.AddSeconds(-1);

        await RunNotificationAsync(fixture, new(TaskActionType.SendNotification, NotificationChannel: "QQBot",
            NotificationTitle: "任务通知 $test.summary", NotificationContent: "结果：$test.summary\n\n&test.image\n$time\n$unknown"), true);

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("任务通知 $test.summary", sent.Message.Title);
        var lines = sent.Message.Content.Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.Equal("结果：已完成", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("$unknown", lines[3]);
        Assert.True(DateTimeOffset.TryParseExact(lines[2], "[yyyy-MM-dd HH:mm:ss]", null,
            System.Globalization.DateTimeStyles.AssumeLocal, out var sendTime));
        Assert.InRange(sendTime, before, DateTimeOffset.Now.AddSeconds(1));
        Assert.Equal(1, calls);
        Assert.NotNull(requested);
        Assert.Equal(2, requested.Count);
        Assert.Contains("$test.summary", requested);
        Assert.Contains("&test.image", requested);
        Assert.Equal(image, notifications.ImageBytes);
        AssertTemporaryNotificationImageRemoved(notifications);
    }

    [Theory]
    [InlineData("app.exe", false)]
    [InlineData("app.BAT", true)]
    [InlineData("app.cmd", false)]
    [InlineData("app.vbs", true)]
    [InlineData("app.py", false)]
    public async Task StartProgramSaveValidatesWithoutLaunchingAndPreservesOptionalFields(string filename, bool withWorkingDirectory)
    {
        await using var fixture = new TaskServiceFixture();
        Directory.CreateDirectory(fixture.Paths.ApplicationDirectory);
        var program = Path.Combine(fixture.Paths.ApplicationDirectory, filename);
        File.WriteAllText(program, "test program");
        var action = new TaskAction(TaskActionType.StartProgram, ProgramPath: program,
            Arguments: "--example", WorkingDirectory: withWorkingDirectory ? fixture.Paths.ApplicationDirectory : "");
        var task = new WinPilotTask("Program", false, new(TaskTriggerType.WinPilotStartup), [action]);
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        using var created = await client.PostAsJsonAsync("/api/tasks", task);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var updated = await client.PutAsJsonAsync("/api/tasks/Program", task with { Name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(action.Arguments, Assert.Single(fixture.Tasks.GetAll()).Actions[0].Arguments);
        Assert.Equal(action.WorkingDirectory, Assert.Single(fixture.Tasks.GetAll()).Actions[0].WorkingDirectory);
        Assert.Empty(fixture.Programs.Requests);
    }

    [Fact]
    public async Task LoadingKeepsTasksWithUnavailableProgramFilesButSavingRejectsThem()
    {
        string stored = "";
        await using var fixture = new TaskServiceFixture(initialize: paths =>
        {
            Directory.CreateDirectory(paths.TasksDirectory);
            var task = new WinPilotTask("Unavailable", false, new(TaskTriggerType.WinPilotStartup),
                [new(TaskActionType.StartProgram, ProgramPath: Path.Combine(paths.ApplicationDirectory, "missing.exe"),
                    WorkingDirectory: Path.Combine(paths.ApplicationDirectory, "missing"))]);
            stored = JsonSerializer.Serialize(TaskPersistenceFile.From(task));
            File.WriteAllText(Path.Combine(paths.TasksDirectory, "Unavailable.json"), stored);
        });
        var loaded = Assert.Single(fixture.Tasks.GetAll());
        Assert.Empty(fixture.Logger.Exceptions);
        var error = Assert.Throws<TaskValidationException>(() => fixture.Tasks.Update(loaded.Name, loaded));
        Assert.Equal("actions[0].programPath", error.Field);
        Assert.Equal("找不到指定的程序文件", error.Message);
        Assert.Equal(stored, File.ReadAllText(Path.Combine(fixture.Paths.TasksDirectory, "Unavailable.json")));
        fixture.Tasks.Delete(loaded.Name);
        Assert.Empty(fixture.Tasks.GetAll());
    }

    private static async Task<WebApplication> CreateTaskAppAsync(TaskServiceFixture fixture)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Tasks);
        builder.Services.AddSingleton(fixture.Extensions);
        builder.Services.AddSingleton<TaskVariableCatalog>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Encoder = ResponseJson.Default.Encoder;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        var app = builder.Build();
        try
        {
            app.MapTaskEndpoints();
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static IDisposable RegisterNotificationImage(TaskServiceFixture fixture, PluginImageContent image)
    {
        var scope = fixture.Extensions.CreateScope("test", "测试插件");
        ((IPluginNotificationVariableApi)scope).Register([new("&test.image", "图片", Type: PluginVariableType.Image)],
            (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["&test.image"] = image }));
        return scope;
    }

    private static void StartNotification(TaskServiceFixture fixture, TaskAction action)
    {
        fixture.Tasks.Create(new("Notification", true, new(TaskTriggerType.WinPilotStartup),
            [action with { ContinueOnFailure = true }, new(TaskActionType.StartProgram, ProgramPath: Environment.ProcessPath!)]));
        fixture.Tasks.RunNow("Notification");
    }

    private static async Task RunNotificationAsync(TaskServiceFixture fixture, TaskAction action, bool succeeds)
    {
        StartNotification(fixture, action);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failures = fixture.Logger.Messages.Where(message => message.StartsWith("任务「Notification」第 1 个动作失败：", StringComparison.Ordinal));
        if (succeeds) Assert.Empty(failures);
        else Assert.Single(failures);
    }

    private static async Task CancelNotificationAsync(TaskServiceFixture fixture, RecordingNotifications notifications, Action? release = null)
    {
        fixture.Logger.ActionCancelled = true;
        var stopping = fixture.Tasks.StopAsync(CancellationToken.None);
        release?.Invoke();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Logger.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(notifications.Token.IsCancellationRequested);
        Assert.Contains("任务「Notification」已取消", fixture.Logger.Messages);
        Assert.Equal(0, fixture.Programs.Launches);
    }

    private static void AssertTemporaryNotificationImageRemoved(RecordingNotifications notifications)
    {
        Assert.True(notifications.Sent.Task.IsCompletedSuccessfully);
        var path = notifications.Sent.Task.Result.Message.ImagePath;
        Assert.NotNull(path);
        Assert.NotNull(notifications.ImageBytes);
        Assert.False(File.Exists(path));
    }

    private sealed class RecordingNotifications(Func<CancellationToken, Task<IReadOnlyList<NotificationChannelResult>>>? send = null) : INotificationService
    {
        internal TaskCompletionSource<(NotificationMessage Message, string? Channel)> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? ImageBytes { get; private set; }
        internal CancellationToken Token { get; private set; }
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, string? channel, CancellationToken cancellationToken = default)
        {
            ImageBytes = message.ImagePath is null ? null : File.ReadAllBytes(message.ImagePath);
            Token = cancellationToken;
            Sent.TrySetResult((message, channel));
            return send?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new("Test", true)]);
        }
    }
}
