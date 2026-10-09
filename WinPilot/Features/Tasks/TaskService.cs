using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using WinPilot.Core;
using WinPilot.Core.Programs;
using WinPilot.Core.Tasks;
using WinPilot.Core.Notifications;
using WinPilot.Features.Desktop;
using WinPilot.Features.Programs;
using WinPilot.Features.Terminal;
using WinPilot.Features.Notifications;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Features.Tasks;

public sealed class TaskService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly PortableDataPaths paths;
    private readonly IProgramManager programs;
    private readonly ProgramTagService tags;
    private readonly KeyboardInputService keyboard;
    private readonly TerminalSessionManager terminals;
    private readonly DesktopScreenshotService screenshots;
    private readonly INotificationService notifications;
    private readonly WindowsProcessEventListener processEvents;
    private readonly PluginTaskExtensionRegistry extensions;
    private readonly TaskVariableCatalog variableCatalog;
    private readonly ILogger<TaskService> logger;
    private readonly TaskCompletionSource pluginsLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private readonly Dictionary<string, WinPilotTask> tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> runningTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> nextCron = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> processPaths = [];
    private bool processDetectionEnabled;
    private bool processListenerStarted;
    private bool taskServiceStarted;
    private bool isStopping;
    private CancellationToken serviceToken;

    public TaskService(PortableDataPaths paths, IProgramManager programs, ProgramTagService tags, KeyboardInputService keyboard, TerminalSessionManager terminals, DesktopScreenshotService screenshots, INotificationService notifications, WindowsProcessEventListener processEvents, ILogger<TaskService> logger, PluginTaskExtensionRegistry? extensions = null, TaskVariableCatalog? variableCatalog = null)
    {
        this.paths = paths;
        this.programs = programs;
        this.tags = tags;
        this.keyboard = keyboard;
        this.terminals = terminals;
        this.screenshots = screenshots;
        this.notifications = notifications;
        this.processEvents = processEvents;
        this.extensions = extensions ?? new(new(), new(), new());
        this.variableCatalog = variableCatalog ?? new(this.extensions);
        this.logger = logger;
        this.processEvents.Changed += ProcessEventsChanged;
        this.extensions.Triggered += ExtensionTriggered;
        Load();
    }

    public IReadOnlyList<WinPilotTask> GetAll()
    {
        lock (gate)
            return tasks.Values.OrderBy(task => task.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public void Create(WinPilotTask task)
    {
        task = Normalize(task);
        Validate(task);
        ValidateNotificationActions(task);
        lock (gate)
        {
            if (tasks.ContainsKey(task.Name))
                throw new InvalidOperationException("同名任务已存在");
            Save(task, false);
            tasks[task.Name] = task;
            ClearRuntimeState(task.Name);
            UpdateProcessDetectionState();
        }
    }

    public void Update(string name, WinPilotTask task)
    {
        task = Normalize(task);
        Validate(task);
        ValidateNotificationActions(task);
        lock (gate)
        {
            if (!tasks.ContainsKey(name))
                throw new FileNotFoundException("任务不存在");
            if (!name.Equals(task.Name, StringComparison.OrdinalIgnoreCase) && tasks.ContainsKey(task.Name))
                throw new InvalidOperationException("同名任务已存在");
            var originalFile = GetTaskFilePath(name);
            var nextFile = GetTaskFilePath(task.Name);
            if (string.Equals(originalFile, nextFile, StringComparison.OrdinalIgnoreCase))
                Save(task);
            else
            {
                File.Move(originalFile, nextFile);
                try { Save(task); }
                catch
                {
                    File.Move(nextFile, originalFile);
                    throw;
                }
            }

            if (!task.Enabled || !name.Equals(task.Name, StringComparison.OrdinalIgnoreCase))
                CancelRunningTask(name);
            tasks.Remove(name);
            ClearRuntimeState(name);
            tasks[task.Name] = task;
            ClearRuntimeState(task.Name);
            UpdateProcessDetectionState();
        }
    }

    public WinPilotTask SetEnabled(string name, bool enabled)
    {
        lock (gate)
        {
            var task = tasks.TryGetValue(name, out var value) ? value : throw new FileNotFoundException("任务不存在");
            if (task.Enabled == enabled)
                return task;
            task = task with
            {
                Enabled = enabled
            };
            Save(task);
            if (!enabled)
                CancelRunningTask(name);
            tasks[name] = task;
            ClearRuntimeState(name);
            UpdateProcessDetectionState();
            return task;
        }
    }

    public void Delete(string name)
    {
        lock (gate)
        {
            if (!tasks.ContainsKey(name))
                throw new FileNotFoundException("任务不存在");
            var file = GetTaskFilePath(name);
            File.Delete(file);
            tasks.Remove(name);
            CancelRunningTask(name);
            ClearRuntimeState(name);
            UpdateProcessDetectionState();
        }
    }

    public void RunNow(string name)
    {
        WinPilotTask task;
        lock (gate)
        {
            task = tasks.TryGetValue(name, out var value) ? value : throw new FileNotFoundException("任务不存在");
        }

        _ = RunAsync(task, "手动运行", CancellationToken.None, null);
    }

    public void NotifyPluginsLoaded() => pluginsLoaded.TrySetResult();

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        lock (gate)
        {
            isStopping = false;
            serviceToken = token;
            taskServiceStarted = true;
            StartProcessListenerIfNeeded();
        }

        logger.LogInformation("任务服务已启动");
        await pluginsLoaded.Task.WaitAsync(token);
        await Task.Delay(500, token);
        foreach (var task in GetAll().Where(task => task.Enabled && task.Trigger.Type == TaskTriggerType.WinPilotStartup))
            Signal(task, "WinPilot 启动", token);
        while (!token.IsCancellationRequested)
        {
            try
            {
                PollCron(token);
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "任务监听失败");
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            isStopping = true;
            taskServiceStarted = false;
            CancelAllRunningTasks();
            StopProcessListener();
            processPaths.Clear();
        }

        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        processEvents.Changed -= ProcessEventsChanged;
        extensions.Triggered -= ExtensionTriggered;
        lock (gate)
        {
            isStopping = true;
            taskServiceStarted = false;
            CancelAllRunningTasks();
            StopProcessListener();
            processPaths.Clear();
        }
        base.Dispose();
    }

    private void PollCron(CancellationToken token) => PollCron(DateTimeOffset.Now, token);

    internal void PollCron(DateTimeOffset now, CancellationToken token)
    {
        foreach (var task in GetAll().Where(task => task.Enabled && task.Trigger.Type == TaskTriggerType.Cron))
        {
            lock (gate)
            {
                if (!nextCron.TryGetValue(task.Name, out var due))
                    nextCron[task.Name] = due = GetNext(task.Trigger.Cron, now);
                if (now < due)
                    continue;
                nextCron[task.Name] = GetNext(task.Trigger.Cron, now.AddSeconds(1));
            }

            Signal(task, $"定时 {task.Trigger.Cron}", token);
        }
    }

    private void Signal(WinPilotTask task, string reason, CancellationToken token) => _ = RunAsync(task, reason, token, null);

    private void ExtensionTriggered(PluginTaskTriggerFired fired)
    {
        try
        {
            WinPilotTask[] matches;
            lock (gate)
                matches = tasks.Values.Where(task => task.Enabled && task.Trigger.Type == TaskTriggerType.Extension && task.Trigger.PluginId?.Equals(fired.PluginId, StringComparison.OrdinalIgnoreCase) == true && task.Trigger.TriggerId?.Equals(fired.TriggerId, StringComparison.OrdinalIgnoreCase) == true && MatchesTriggerParameters(task.Trigger.Parameters, fired.Variables)).ToArray();
            foreach (var task in matches)
                _ = RunAsync(task, $"扩展触发 {fired.TriggerId}", fired.CancellationToken, fired);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "处理插件任务触发失败，插件 ID：{PluginId}，触发器 ID：{TriggerId}", fired.PluginId, fired.TriggerId);
        }
    }

    private static bool MatchesTriggerParameters(IReadOnlyDictionary<string, object?>? expected, IReadOnlyDictionary<string, object?> actual) => expected is null || expected.All(item => actual.TryGetValue(item.Key, out var value) && string.Equals(Convert.ToString(item.Value), Convert.ToString(value), StringComparison.Ordinal));

    private async Task RunAsync(WinPilotTask task, string reason, CancellationToken token, PluginTaskTriggerFired? fired)
    {
        CancellationTokenSource instance;
        lock (gate)
        {
            if (isStopping || token.IsCancellationRequested ||
                !tasks.TryGetValue(task.Name, out var current) || !ReferenceEquals(current, task) ||
                runningTasks.ContainsKey(task.Name))
                return;
            instance = CancellationTokenSource.CreateLinkedTokenSource(token);
            runningTasks.Add(task.Name, instance);
        }

        try
        {
            var timestamp = fired?.Timestamp ?? DateTimeOffset.Now;
            var variables = new Dictionary<string, object?>(fired?.Variables ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
            variables["$time"] = $"[{timestamp:yyyy-MM-dd HH:mm:ss}]";
            var context = new PluginTaskRunContext(task.Name, fired?.PluginId, fired?.TriggerId, timestamp, new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(variables), instance.Token);
            logger.LogInformation("任务「{Name}」开始执行，触发：{Reason}", task.Name, reason);
            var failedCount = 0;
            var stoppedAfterFailure = false;
            for (var index = 0; index < task.Actions.Count; index++)
            {
                var action = task.Actions[index];
                var description = DescribeAction(action);
                try
                {
                    logger.LogInformation("任务「{Name}」第 {Index} 个动作开始：{Action}", task.Name, index + 1, description);
                    instance.Token.ThrowIfCancellationRequested();
                    await Execute(action, context);
                }
                catch (OperationCanceledException) when (instance.IsCancellationRequested)
                {
                    logger.LogInformation("任务「{Name}」已取消", task.Name);
                    return;
                }
                catch (Exception exception)
                {
                    failedCount++;
                    logger.LogError(exception, action.ContinueOnFailure ? "任务「{Name}」第 {Index} 个动作失败：{Action}，继续执行后续动作" : "任务「{Name}」第 {Index} 个动作失败：{Action}，停止执行后续动作", task.Name, index + 1, description);
                    if (!action.ContinueOnFailure)
                    {
                        stoppedAfterFailure = true;
                        break;
                    }
                }
            }

            if (failedCount == 0)
                logger.LogInformation("任务「{Name}」执行完成", task.Name);
            else if (stoppedAfterFailure)
                logger.LogWarning("任务「{Name}」执行结束，共 {Count} 个动作失败，后续动作未执行", task.Name, failedCount);
            else
                logger.LogWarning("任务「{Name}」执行完成，共 {Count} 个动作失败", task.Name, failedCount);
        }
        finally
        {
            lock (gate)
            {
                runningTasks.Remove(task.Name);
            }

            instance.Dispose();
        }
    }

    private async Task Execute(TaskAction action, PluginTaskRunContext context)
    {
        var token = context.CancellationToken;
        switch (action.Type)
        {
            case TaskActionType.StartProgram:
                programs.Launch(new(action.ProgramPath ?? "", action.Arguments ?? "", action.WorkingDirectory ?? "", action.RunElevated, action.Silent));
                break;
            case TaskActionType.StartTag:
                var tag = tags.Get(action.TagName ?? "");
                programs.Launch(new(tag.ProgramPath, tag.Arguments, tag.WorkingDirectory, tag.RunElevated, tag.Silent));
                break;
            case TaskActionType.CloseProcess:
                var ids = GetCloseProcessIds(action.ProcessName, action.CurrentUserOnly);
                if (ids.Length == 0)
                    throw new InvalidOperationException("未找到目标进程");
                programs.Close(ids, action.Force);
                break;
            case TaskActionType.Delay:
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, action.DelaySeconds)), token);
                break;
            case TaskActionType.Keyboard:
                keyboard.Send(action.Keys);
                break;
            case TaskActionType.Shell:
                var result = await terminals.ExecuteTaskCommandAsync(action.Shell ?? "", action.Command ?? "", token);
                logger.LogInformation("终端命令{Result}，终端类型：{Shell}", result.ReceivedOutput ? "已收到输出" : "等待输出超时，暂未确认执行结果", result.Shell);
                break;
            case TaskActionType.SendNotification:
                await SendConfiguredNotification(action, context, null, false);
                break;
            case TaskActionType.SendNotificationToChannel:
                await SendConfiguredNotification(action, context, action.NotificationChannel, true);
                break;
            case TaskActionType.Extension:
                await ExecuteExtension(action, context);
                break;
        }
    }

    private async Task SendConfiguredNotification(TaskAction action, PluginTaskRunContext context, string? channel, bool ignoreEnabled, PluginImageContent? pluginImage = null)
    {
        var (contentTemplate, directImagePath) = ExtractDirectImagePath(action.NotificationContent);
        var contentNames = VariableResolver.ExtractNames(contentTemplate);
        var contentImageNames = ValidateImageVariables(contentTemplate, variableCatalog);
        if ((directImagePath is not null && contentImageNames.Length > 0) || (pluginImage is not null && (directImagePath is not null || contentImageNames.Length > 0)))
            throw new InvalidOperationException("图片仅允许一个");
        var requested = contentNames.Where(name => !name.Equals("$time", StringComparison.OrdinalIgnoreCase) && !name.Equals("&screenshot", StringComparison.OrdinalIgnoreCase) && !context.Variables.ContainsKey(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var variables = new Dictionary<string, object?>(context.Variables, StringComparer.OrdinalIgnoreCase);
        if (requested.Length > 0)
        {
            var resolved = await extensions.ResolveVariablesAsync(requested, context, context.CancellationToken);
            foreach (var (name, value) in resolved)
                variables[name] = value;
        }

        var resolvedContext = context with
        {
            Variables = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(variables)
        };
        if (contentImageNames.Contains("&screenshot", StringComparer.OrdinalIgnoreCase))
        {
            var screenshot = screenshots.Capture();
            variables["&screenshot"] = new PluginImageContent(screenshot.Content, screenshot.ContentType);
        }

        var textTemplate = contentImageNames.Length == 0 ? contentTemplate : RemoveVariableLine(contentTemplate, contentImageNames[0]);
        var content = ResolveNotificationContent(textTemplate, resolvedContext);
        var imageTemplate = directImagePath ?? (contentImageNames.Length == 1 ? contentImageNames[0] : null);
        var (imagePath, temporaryImagePath) = pluginImage is null ? await ResolveNotificationImageAsync(imageTemplate, resolvedContext) : await ResolvePluginImageAsync(pluginImage, context.CancellationToken);
        try
        {
            await SendNotification(new(action.NotificationTitle ?? "WinPilot 通知", content, imagePath), channel, context.CancellationToken, ignoreEnabled);
            if (temporaryImagePath is not null)
                await Task.Delay(TimeSpan.FromSeconds(2), context.CancellationToken);
        }
        finally
        {
            if (temporaryImagePath is not null)
                try
                {
                    File.Delete(temporaryImagePath);
                }
                catch
                {
                }
        }
    }

    private static async Task<(string? ImagePath, string? TemporaryImagePath)> ResolvePluginImageAsync(PluginImageContent image, CancellationToken token)
    {
        var path = await PluginNotificationDelivery.WriteImageAsync(image, token);
        return (path, path);
    }

    internal static async Task<(string? ImagePath, string? TemporaryImagePath)> ResolveNotificationImageAsync(string? template, PluginTaskRunContext context)
    {
        if (string.IsNullOrWhiteSpace(template))
            return (null, null);
        var names = VariableResolver.ExtractNames(template);
        if (names.Count == 1 && template.Trim().Equals(names[0], StringComparison.OrdinalIgnoreCase) && context.Variables.TryGetValue(names[0], out var value) && value is PluginImageContent image)
        {
            if (image.Content.Length == 0)
                return (null, null);
            if (image.Content.Length > 10 * 1024 * 1024)
                throw new InvalidOperationException("通知图片不能超过 10 MB");
            var extension = image.ContentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                _ => throw new InvalidOperationException("通知图片仅支持 PNG 或 JPEG")
            };
            var path = Path.Combine(Path.GetTempPath(), $"WinPilot-notification-{Guid.NewGuid():N}{extension}");
            try
            {
                await File.WriteAllBytesAsync(path, image.Content, context.CancellationToken);
                return (path, path);
            }
            catch
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }

                throw;
            }
        }

        if (names.Count == 1 && template.Trim().Equals(names[0], StringComparison.OrdinalIgnoreCase) && IsImageVariableReference(names[0]))
            throw new InvalidOperationException($"图片变量 {names[0]} 不可用");
        if (names.Any(name => context.Variables.TryGetValue(name, out var value) && value is PluginImageContent))
            throw new InvalidOperationException("图片变量必须单独填写，不能与其他文字组合");
        var pathValue = VariableResolver.Resolve(template, context).Trim();
        if (string.IsNullOrWhiteSpace(pathValue))
            return (null, null);
        ValidateImagePath(pathValue);
        return (pathValue, null);
    }

    internal static (string? Content, string? ImagePath) ExtractDirectImagePath(string? template)
    {
        if (string.IsNullOrEmpty(template))
            return (template, null);
        if (CountOccurrences(template, "&image=") > 1)
            throw new InvalidOperationException("图片仅允许一个");
        string? imagePath = null;
        var content = new List<string>();
        foreach (var line in template.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("&image=", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(line);
                continue;
            }

            if (imagePath is not null)
                throw new InvalidOperationException("图片仅允许一个");
            var value = trimmed["&image=".Length..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            else if (value.Contains('"'))
                throw new InvalidOperationException("图片路径引号不完整");
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("图片路径不能为空");
            imagePath = value;
        }

        return (string.Join(Environment.NewLine, content), imagePath);
    }

    private static bool IsImageVariableReference(string name) => name.StartsWith('&') && !name.Equals("&image", StringComparison.OrdinalIgnoreCase);

    private static int CountOccurrences(string value, string target)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(target, index, StringComparison.OrdinalIgnoreCase)) >= 0; index += target.Length)
            count++;
        return count;
    }

    private static string RemoveVariableLine(string? template, string name) => string.Join(Environment.NewLine, (template ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(line => !line.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)));

    private static void ValidateImagePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("图片路径必须是绝对路径");
        if (!File.Exists(path))
            throw new FileNotFoundException("通知图片不存在", path);
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("通知图片仅支持 PNG 或 JPEG");
        if (new FileInfo(path).Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("通知图片不能超过 10 MB");
    }

    internal static string ResolveNotificationContent(string? template, PluginTaskRunContext context)
    {
        var variables = new Dictionary<string, object?>(context.Variables, StringComparer.Ordinal)
        {
            ["$time"] = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}]"
        };
        var notificationContext = context with
        {
            Variables = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(variables)
        };
        return VariableResolver.Resolve(template, notificationContext);
    }

    private async Task SendNotification(NotificationMessage message, string? channel, CancellationToken token, bool ignoreEnabled)
    {
        if (string.IsNullOrWhiteSpace(message.Content) && string.IsNullOrWhiteSpace(message.ImagePath))
            throw new InvalidOperationException("请输入通知内容或通知图片");
        ValidateNotificationResults(await notifications.SendAsync(message, token, channel, ignoreEnabled));
    }

    internal static void ValidateNotificationResults(IReadOnlyList<NotificationChannelResult> results)
    {
        if (results.Count == 0)
            throw new InvalidOperationException("没有可用的通知渠道");
        var failed = results.Where(result => !result.Succeeded).Select(result => result.Channel).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (failed.Length > 0)
            throw new InvalidOperationException($"通知发送失败：{string.Join('、', failed)}");
    }

    private static string DescribeAction(TaskAction action) => action.Type switch
    {
        TaskActionType.StartProgram => $"启动程序「{action.ProgramPath}」",
        TaskActionType.StartTag => $"启动快捷标签「{action.TagName}」",
        TaskActionType.CloseProcess => $"{(action.Force ? "强制关闭" : "关闭")}进程「{action.ProcessName}」",
        TaskActionType.Delay => $"延迟 {action.DelaySeconds} 秒",
        TaskActionType.Keyboard => "发送按键",
        TaskActionType.Shell => $"Shell「{action.Shell}」执行命令",
        TaskActionType.SendNotification => "发送通知",
        TaskActionType.SendNotificationToChannel => $"发送通知到 {action.NotificationChannel}",
        TaskActionType.Extension => "插件扩展",
        _ => action.Type.ToString()
    };

    private void ProcessEventsChanged(object? sender, WindowsProcessEvent processEvent)
    {
        try
        {
            IReadOnlyList<WinPilotTask> processTasks;
            IReadOnlyList<WinPilotTask> allProcessTasks;
            var expectedType = processEvent.Type == WindowsProcessEventType.Started ? TaskTriggerType.ProcessStarted : TaskTriggerType.ProcessExited;
            lock (gate)
            {
                if (!processDetectionEnabled || !processListenerStarted)
                    return;
                allProcessTasks = tasks.Values.Where(task => task.Enabled && IsProcessTrigger(task.Trigger)).ToArray();
                processTasks = allProcessTasks.Where(task => task.Trigger.Type == expectedType).ToArray();
            }

            var cachePaths = allProcessTasks.SelectMany(task => task.Trigger.ProcessPaths ?? []).Where(path => Path.GetFileNameWithoutExtension(path).Equals(processEvent.ProcessName, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var processPath = "";
            if (processEvent.Type == WindowsProcessEventType.Started && cachePaths.Length > 0)
                processPath = GetProcessPath(processEvent.ProcessId);
            lock (gate)
            {
                if (processEvent.Type == WindowsProcessEventType.Started && cachePaths.Length > 0)
                    processPaths[processEvent.ProcessId] = processPath;
                if (processEvent.Type == WindowsProcessEventType.Exited && processPaths.Remove(processEvent.ProcessId, out var known))
                    processPath = known;
            }

            foreach (var task in processTasks)
            {
                if (!Matches(task.Trigger.ProcessPaths, processEvent.ProcessName, processPath))
                    continue;
                Signal(task, expectedType == TaskTriggerType.ProcessStarted ? "当程序启动时" : "当程序关闭时", serviceToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "处理进程事件失败，进程 ID：{ProcessId}，进程名称：{ProcessName}，事件类型：{Type}", processEvent.ProcessId, processEvent.ProcessName, processEvent.Type);
        }
    }

    private void UpdateProcessDetectionState()
    {
        var enabled = tasks.Values.Any(task => task.Enabled && IsProcessTrigger(task.Trigger));
        if (enabled == processDetectionEnabled)
        {
            if (enabled && taskServiceStarted)
                PrimeProcessPaths();
            return;
        }

        processDetectionEnabled = enabled;
        processPaths.Clear();
        if (!taskServiceStarted)
            return;
        if (enabled)
            StartProcessListenerIfNeeded();
        else
            StopProcessListener();
    }

    private void StartProcessListenerIfNeeded()
    {
        if (!processDetectionEnabled || processListenerStarted)
            return;
        if (!processEvents.Start())
            return;
        processListenerStarted = true;
        PrimeProcessPaths();
        logger.LogInformation("任务触发所需的 Windows 进程监听已启动");
    }

    private void StopProcessListener()
    {
        if (!processListenerStarted)
            return;
        processEvents.Stop();
        processListenerStarted = false;
        processPaths.Clear();
        logger.LogInformation("任务触发所需的 Windows 进程监听已停止");
    }

    private void PrimeProcessPaths()
    {
        var names = tasks.Values.Where(task => task.Enabled && IsProcessTrigger(task.Trigger)).SelectMany(task => task.Trigger.ProcessPaths ?? []).Select(Path.GetFileNameWithoutExtension).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var name in names)
            foreach (var process in Process.GetProcessesByName(name))
                try
                {
                    var path = SafePath(process);
                    if (!string.IsNullOrWhiteSpace(path))
                        processPaths[process.Id] = path;
                }
                finally
                {
                    process.Dispose();
                }
    }

    private static bool IsProcessTrigger(TaskTrigger trigger) => trigger.Type is TaskTriggerType.ProcessStarted or TaskTriggerType.ProcessExited;

    private int[] GetCloseProcessIds(string? name, bool currentUserOnly)
    {
        var processName = Path.GetFileNameWithoutExtension(name ?? "");
        if (currentUserOnly)
        {
            using var identity = WindowsIdentity.GetCurrent();
            return programs.GetRunningPrograms(identity.Name)
                .Where(program => string.Equals(Path.GetFileNameWithoutExtension(program.Name), processName, StringComparison.OrdinalIgnoreCase))
                .Select(program => program.ProcessId).ToArray();
        }
        var processes = Process.GetProcessesByName(processName);
        try { return processes.Select(process => process.Id).ToArray(); }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static string GetProcessPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return SafePath(process);
        }
        catch { return ""; }
    }

    private static string SafePath(Process process)
    {
        try
        {
            return Path.GetFullPath(process.MainModule?.FileName ?? "");
        }
        catch
        {
            return "";
        }
    }

    internal static bool Matches(IReadOnlyList<string>? paths, string name, string path) => paths?.Any(expected => Path.GetFileNameWithoutExtension(expected).Equals(name, StringComparison.OrdinalIgnoreCase) && expected.Equals(path, StringComparison.OrdinalIgnoreCase)) == true;

    private static DateTimeOffset GetNext(string? expression, DateTimeOffset now) => CronExpression.Parse(expression ?? throw new InvalidOperationException("Cron 表达式不能为空")).GetNextOccurrence(now, TimeZoneInfo.Local) ?? throw new InvalidOperationException("Cron 表达式没有下一次执行时间");

    private void Load()
    {
        if (Directory.Exists(paths.TasksDirectory))
            foreach (var file in Directory.EnumerateFiles(paths.TasksDirectory, "*.json"))
                try
                {
                    var stored = JsonSerializer.Deserialize<TaskPersistenceFile>(File.ReadAllText(file), JsonOptions);
                    if (stored is not null)
                    {
                        var task = Normalize(stored.ToTask());
                        Validate(task);
                        tasks[task.Name] = task;
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "无法读取任务文件 {File}", file);
                }

        UpdateProcessDetectionState();
    }

    private static WinPilotTask Normalize(WinPilotTask task) => task with
    {
        Trigger = task.Trigger with
        {
            Cron = task.Trigger.Cron ?? "",
            ProcessPaths = (task.Trigger.ProcessPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        },
        Actions = task.Actions.Select(action => action with { ProgramPath = action.ProgramPath ?? "", Arguments = action.Arguments ?? "", WorkingDirectory = action.WorkingDirectory ?? "", TagName = action.TagName ?? "", ProcessName = action.ProcessName ?? "", Keys = action.Keys ?? [], Shell = action.Shell ?? "", Command = action.Command ?? "", NotificationTitle = action.NotificationTitle ?? "", NotificationContent = action.NotificationContent ?? "", NotificationChannel = action.NotificationChannel ?? "" }).ToArray()
    };

    private void ClearRuntimeState(string name) => nextCron.Remove(name);

    private void CancelRunningTask(string name)
    {
        if (runningTasks.TryGetValue(name, out var instance)) instance.Cancel();
    }

    private void CancelAllRunningTasks()
    {
        foreach (var instance in runningTasks.Values.ToArray()) instance.Cancel();
    }

    private void Save(WinPilotTask task, bool overwrite = true)
    {
        Directory.CreateDirectory(paths.TasksDirectory);
        var file = GetTaskFilePath(task.Name);
        var temporaryFile = file + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(TaskPersistenceFile.From(task), JsonOptions));
            File.Move(temporaryFile, file, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
        }
    }

    private string GetTaskFilePath(string name)
    {
        ValidateName(name);
        return Path.Combine(paths.TasksDirectory, name + ".json");
    }

    private static void Validate(WinPilotTask task)
    {
        ValidateName(task.Name);
        if (!Enum.IsDefined(task.Trigger.Type) || task.Actions.Any(action => !Enum.IsDefined(action.Type)))
            throw new InvalidOperationException("任务类型无效");
        if (task.Actions.Count == 0)
            throw new InvalidOperationException("至少需要一个动作");
        if (task.Trigger.Type == TaskTriggerType.Cron)
        {
            if (string.IsNullOrWhiteSpace(task.Trigger.Cron))
                throw new InvalidOperationException("请输入 Cron 表达式");
            try
            {
                CronExpression.Parse(task.Trigger.Cron);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new InvalidOperationException("Cron 表达式格式无效", exception);
            }
        }
        else if (IsProcessTrigger(task.Trigger))
        {
            var paths = task.Trigger.ProcessPaths ?? [];
            if (paths.Count == 0)
                throw new InvalidOperationException("请至少选择一个程序绝对路径");
            if (paths.Any(path => !Path.IsPathFullyQualified(path)))
                throw new InvalidOperationException("程序路径必须是绝对路径");
        }

        foreach (var action in task.Actions)
            switch (action.Type)
            {
                case TaskActionType.StartProgram when string.IsNullOrWhiteSpace(action.ProgramPath):
                    throw new InvalidOperationException("请选择要启动的程序");
                case TaskActionType.StartTag when string.IsNullOrWhiteSpace(action.TagName):
                    throw new InvalidOperationException("请选择快捷标签");
                case TaskActionType.CloseProcess when string.IsNullOrWhiteSpace(action.ProcessName):
                    throw new InvalidOperationException("请输入进程名");
                case TaskActionType.Delay when action.DelaySeconds <= 0:
                    throw new InvalidOperationException("请输入有效的延迟秒数");
                case TaskActionType.Keyboard when action.Keys is null || action.Keys.Count == 0:
                    throw new InvalidOperationException("请选择至少一个按键");
                case TaskActionType.Shell when !IsSupportedShell(action.Shell):
                    throw new InvalidOperationException("Shell 仅支持 CMD 或 PowerShell");
                case TaskActionType.Shell when string.IsNullOrWhiteSpace(action.Command):
                    throw new InvalidOperationException("请输入 Shell 命令");
                case TaskActionType.SendNotification or TaskActionType.SendNotificationToChannel when string.IsNullOrWhiteSpace(action.NotificationContent):
                    throw new InvalidOperationException("请输入通知内容或图片变量");
                case TaskActionType.SendNotificationToChannel when string.IsNullOrWhiteSpace(action.NotificationChannel):
                    throw new InvalidOperationException("请选择通知渠道");
            }
    }

    private static bool IsSupportedShell(string? shell) => shell is not null && (shell.Equals("cmd", StringComparison.OrdinalIgnoreCase) || shell.Equals("powershell", StringComparison.OrdinalIgnoreCase));

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidOperationException("任务名称无效");
    }

    private async Task ExecuteExtension(TaskAction action, PluginTaskRunContext context)
    {
        if (string.IsNullOrWhiteSpace(action.PluginId) || string.IsNullOrWhiteSpace(action.ActionId) || !extensions.Actions.TryGet(action.PluginId, action.ActionId, out var definition))
            throw new InvalidOperationException("扩展动作不可用");
        var parameters = action.Parameters ?? new Dictionary<string, object?>();
        var schema = definition.Parameters;
        if (definition.OptionsResolver is not null)
        {
            var current = new List<PluginActionParameterDefinition>(schema.Count);
            foreach (var parameter in schema)
            {
                var options = parameter.Type is PluginActionParameterType.Select or PluginActionParameterType.MultiCheckbox ? await extensions.GetActionOptionsAsync(action.PluginId, action.ActionId, parameter.Key, context.CancellationToken) : parameter.Options;
                current.Add(parameter with { Options = options });
            }

            schema = current;
        }

        ValidateActionParameters(schema, parameters);
        if (definition.HostActionPlan is not null)
        {
            foreach (var step in definition.HostActionPlan.Steps)
                await ExecuteHostStep(step, parameters, context);
            return;
        }

        if (definition.Handler is null)
            throw new InvalidOperationException("扩展动作不可用");
        var result = await definition.Handler(new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(parameters)), context, context.CancellationToken);
        if (result.Status != PluginTaskActionResultStatus.Success)
            throw new InvalidOperationException(result.Error ?? result.Status.ToString());
        if (result.HostActionPlan is not null)
        {
            PluginTaskActionRegistry.ValidatePlan(result.HostActionPlan);
            foreach (var step in result.HostActionPlan.Steps)
                await ExecuteHostStep(step, parameters, context);
        }
    }

    private async Task ExecuteHostStep(PluginHostActionStep step, IReadOnlyDictionary<string, object?> extensionParameters, PluginTaskRunContext context)
    {
        var values = step.Parameters.ToDictionary(pair => pair.Key, pair => ResolvePlanValue(pair.Value, extensionParameters));
        if (step.Type == PluginHostActionType.SendNotification)
        {
            var channel = GetString(values, "channel");
            var image = values.TryGetValue("image", out var imageValue) ? imageValue as PluginImageContent : null;
            if (values.TryGetValue("image", out imageValue) && imageValue is not null && image is null)
                throw new InvalidOperationException("image 必须是 PluginImageContent");
            var notification = new TaskAction(string.IsNullOrWhiteSpace(channel) ? TaskActionType.SendNotification : TaskActionType.SendNotificationToChannel, NotificationTitle: GetString(values, "title") ?? "", NotificationContent: GetString(values, "content") ?? "", NotificationChannel: channel);
            await SendConfiguredNotification(notification, context, channel, !string.IsNullOrWhiteSpace(channel), image);
            return;
        }

        var action = step.Type switch
        {
            PluginHostActionType.StartProgram => new TaskAction(TaskActionType.StartProgram, ProgramPath: GetString(values, "programPath"), Arguments: GetString(values, "arguments"), WorkingDirectory: GetString(values, "workingDirectory"), RunElevated: GetBool(values, "runElevated"), Silent: GetBool(values, "silent")),
            PluginHostActionType.Delay => new TaskAction(TaskActionType.Delay, DelaySeconds: GetInt(values, "delaySeconds")),
            PluginHostActionType.Keyboard => new TaskAction(TaskActionType.Keyboard, Keys: GetStrings(values, "keys")),
            PluginHostActionType.Shell => new TaskAction(TaskActionType.Shell, Shell: GetString(values, "shell"), Command: GetString(values, "command")),
            _ => throw new InvalidOperationException("Host Action 类型不支持")
        };
        await Execute(action, context);
    }

    private static object? ResolvePlanValue(object? value, IReadOnlyDictionary<string, object?> parameters) => value is string text && text.StartsWith("$param.", StringComparison.Ordinal) && parameters.TryGetValue(text[7..], out var resolved) ? resolved : value;

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) => values.TryGetValue(key, out var value) ? Convert.ToString(value) : null;

    private static bool GetBool(IReadOnlyDictionary<string, object?> values, string key) => values.TryGetValue(key, out var value) && Convert.ToBoolean(value);

    private static int GetInt(IReadOnlyDictionary<string, object?> values, string key) => values.TryGetValue(key, out var value) ? Convert.ToInt32(value) : 0;

    private static IReadOnlyList<string> GetStrings(IReadOnlyDictionary<string, object?> values, string key) => values.TryGetValue(key, out var value) && value is IEnumerable<string> strings ? strings.ToArray() : [];

    private static void ValidateActionParameters(IReadOnlyList<PluginActionParameterDefinition> schema, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var value in values.Keys.Where(key => !schema.Any(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException($"未知动作参数：{value}");
        foreach (var parameter in schema)
        {
            values.TryGetValue(parameter.Key, out var value);
            var selected = ParameterStrings(value);
            if (parameter.Required && (value is null || (parameter.Type == PluginActionParameterType.MultiCheckbox ? selected.Length == 0 : string.IsNullOrWhiteSpace(Convert.ToString(value)))))
                throw new InvalidOperationException($"缺少动作参数：{parameter.Key}");
            if (value is null && parameter.DefaultValue is not null)
                continue;
            if (value is not null && parameter.Type == PluginActionParameterType.Number && !decimal.TryParse(Convert.ToString(value), out _))
                throw new InvalidOperationException($"动作参数类型无效：{parameter.Key}");
            if (value is not null && parameter.Type == PluginActionParameterType.Select && parameter.Options?.All(option => !option.Value.Equals(Convert.ToString(value), StringComparison.Ordinal)) == true)
                throw new InvalidOperationException($"动作参数选项无效：{parameter.Key}");
            if (value is not null && parameter.Type == PluginActionParameterType.MultiCheckbox && (selected.Length == 0 || selected.Any(item => parameter.Options?.All(option => !option.Value.Equals(item, StringComparison.Ordinal)) != false)))
                throw new InvalidOperationException($"动作参数选项无效：{parameter.Key}");
        }
    }

    private static string[] ParameterStrings(object? value)
    {
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray();
        return value is IEnumerable<object?> items ? items.Select(Convert.ToString).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray() : [];
    }

    private void ValidateNotificationActions(WinPilotTask task)
    {
        foreach (var action in task.Actions.Where(action => action.Type is TaskActionType.SendNotification or TaskActionType.SendNotificationToChannel))
            ValidateNotificationImages(action.NotificationContent, variableCatalog);
    }

    internal static void ValidateNotificationImages(string? content, TaskVariableCatalog variableCatalog)
    {
        var (text, imagePath) = ExtractDirectImagePath(content);
        var images = ValidateImageVariables(text, variableCatalog);
        if (imagePath is not null && images.Length > 0)
            throw new InvalidOperationException("图片仅允许一个");
    }

    internal static string[] ValidateImageVariables(string? content, TaskVariableCatalog variableCatalog)
    {
        var images = VariableResolver.ExtractOccurrences(content).Select(variableCatalog.MatchRegisteredImage).Where(name => name is not null).Cast<string>().ToArray();
        if (images.Length > 1)
            throw new InvalidOperationException("图片仅允许一个");
        if (images.Length == 1 && (content ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.None).All(line => !line.Trim().Equals(images[0], StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("图片变量必须单独占一行");
        return images;
    }
}
