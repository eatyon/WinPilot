using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Cronos;
using Microsoft.Win32;
using WinPilot.Core;
using WinPilot.Core.Input;
using WinPilot.Core.Notifications;
using WinPilot.Core.Programs;
using WinPilot.Core.Tasks;
using WinPilot.Features.Desktop;
using WinPilot.Features.Notifications;
using WinPilot.Features.Plugins;
using WinPilot.Features.Programs;
using WinPilot.Features.Terminal;
using WinPilot.PluginContract;
using WinPilot.Windows;

namespace WinPilot.Features.Tasks;

/// <summary>
/// 管理任务定义、按需触发监听和执行实例；同名任务不重入，动作按配置顺序执行。
/// </summary>
public sealed class TaskService : BackgroundService
{
    private readonly TaskDefinitionStore store;
    private readonly IProgramManager programs;
    private readonly ProgramTagService tags;
    private readonly KeyboardInputService keyboard;
    private readonly TerminalSessionManager terminals;
    private readonly TaskNotificationSender notificationSender;
    private readonly WindowsProcessEventListener processEvents;
    private readonly WindowsKeyboardEventListener? keyboardEvents;
    private readonly PluginTaskExtensionRegistry extensions;
    private readonly ILogger<TaskService> logger;
    private readonly TimeProvider clock;

    private readonly TaskCompletionSource pluginsLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private readonly Dictionary<string, WinPilotTask> tasks = new(StringComparer.OrdinalIgnoreCase);
    // 同名任务共用运行占位；在真正执行前登记，防止多个触发源同时启动。
    private readonly Dictionary<string, TaskExecution> runningTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenRegistration stoppingRegistration;
    private CancellationToken serviceToken;
    private bool taskServiceStarted;
    private bool isStopping;

    private readonly Dictionary<string, DateTimeOffset?> nextCron = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (WinPilotTask Task, CronExpression Expression)> cronExpressions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> cronRetryStartedAt = new(StringComparer.OrdinalIgnoreCase);
    private TimeZoneInfo cronTimeZone;
    private CronSchedulerRun? cronSchedulerRun;
    private bool cronReady;

    private readonly Dictionary<int, string> processPaths = [];
    private bool processDetectionEnabled;
    private bool processListenerStarted;
    private long processListenerGeneration;

    public TaskService(
        PortableDataPaths paths,
        IProgramManager programs,
        ProgramTagService tags,
        KeyboardInputService keyboard,
        TerminalSessionManager terminals,
        DesktopScreenshotService screenshots,
        INotificationService notifications,
        WindowsProcessEventListener processEvents,
        ILogger<TaskService> logger,
        PluginTaskExtensionRegistry? extensions = null,
        TaskVariableCatalog? variableCatalog = null,
        IHostApplicationLifetime? lifetime = null,
        TimeProvider? clock = null,
        WindowsKeyboardEventListener? keyboardEvents = null)
    {
        store = new(paths);
        this.programs = programs;
        this.tags = tags;
        this.keyboard = keyboard;
        this.terminals = terminals;
        this.processEvents = processEvents;
        this.keyboardEvents = keyboardEvents;
        if (keyboardEvents is not null)
        {
            keyboardEvents.Triggered += KeyboardTriggered;
        }
        this.extensions = extensions ?? new(new(), new(), new());
        notificationSender = new(screenshots, notifications, this.extensions, variableCatalog ?? new(this.extensions), logger);
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;
        cronTimeZone = this.clock.LocalTimeZone;
        this.processEvents.Changed += ProcessEventsChanged;
        this.extensions.Triggered += ExtensionTriggered;
        Load();
        stoppingRegistration = lifetime?.ApplicationStopping.Register(BeginStopping) ?? default;
    }

    public IReadOnlyList<WinPilotTask> GetAll()
    {
        lock (gate)
        {
            return tasks.Values.OrderBy(task => task.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    internal bool? GetKeyboardAvailability(WinPilotTask task)
    {
        lock (gate)
        {
            if (task.Trigger.Type != TaskTriggerType.Keyboard
                || !task.Enabled
                || !taskServiceStarted
                || isStopping
                || !tasks.TryGetValue(task.Name, out var current)
                || !ReferenceEquals(current, task))
            {
                return null;
            }
            return keyboardEvents?.Availability;
        }
    }

    internal bool? GetCronAvailability(WinPilotTask task)
    {
        lock (gate)
        {
            if (task.Trigger.Type != TaskTriggerType.Cron
                || !tasks.TryGetValue(task.Name, out var current)
                || !ReferenceEquals(current, task))
            {
                return null;
            }
            if (nextCron.TryGetValue(task.Name, out var due) && due is null)
            {
                return false;
            }
            return GetNextCronOccurrence(task, clock.GetUtcNow(), clock.LocalTimeZone) is not null;
        }
    }

    public WinPilotTask Create(WinPilotTask task)
    {
        task = TaskDefinitionValidator.Normalize(task);
        var cron = TaskDefinitionValidator.Validate(task);
        TaskDefinitionValidator.ValidateProgramConfigurations(task);
        notificationSender.ValidateActions(task);
        lock (gate)
        {
            if (tasks.ContainsKey(task.Name))
            {
                throw new ResourceNameConflictException("同名任务已存在");
            }
            if (File.Exists(store.GetFilePath(task.Name)))
            {
                throw new ResourceNameConflictException("同名任务文件已存在");
            }
            store.Save(task, false);
            tasks[task.Name] = task;
            ClearCronState(task.Name);
            if (cron is not null)
            {
                cronExpressions[task.Name] = (task, cron);
            }
            UpdateProcessDetectionState();
            UpdateKeyboardDetectionState();
            RefreshCronSchedule();
        }
        return task;
    }

    public WinPilotTask Update(string name, WinPilotTask task)
    {
        task = TaskDefinitionValidator.Normalize(task);
        var cron = TaskDefinitionValidator.Validate(task);
        TaskDefinitionValidator.ValidateProgramConfigurations(task);
        notificationSender.ValidateActions(task);
        TaskExecution? cancellation = null;
        try
        {
            lock (gate)
            {
                if (!tasks.ContainsKey(name))
                {
                    throw new FileNotFoundException("任务不存在");
                }
                if (!name.Equals(task.Name, StringComparison.OrdinalIgnoreCase) && tasks.ContainsKey(task.Name))
                {
                    throw new ResourceNameConflictException("同名任务已存在");
                }
                var originalFile = store.GetFilePath(name);
                var nextFile = store.GetFilePath(task.Name);
                if (string.Equals(originalFile, nextFile, StringComparison.OrdinalIgnoreCase))
                {
                    store.Save(task);
                }
                else
                {
                    if (File.Exists(nextFile))
                    {
                        throw new ResourceNameConflictException("同名任务文件已存在");
                    }
                    File.Move(originalFile, nextFile);
                    FileOperationRecovery.WithRollback(() => store.Save(task),
                        () => File.Move(nextFile, originalFile));
                }

                if (!name.Equals(task.Name, StringComparison.OrdinalIgnoreCase))
                {
                    cancellation = RequestTaskCancellation(name);
                }
                tasks.Remove(name);
                ClearCronState(name);
                tasks[task.Name] = task;
                ClearCronState(task.Name);
                if (cron is not null)
                {
                    cronExpressions[task.Name] = (task, cron);
                }
                UpdateProcessDetectionState();
                UpdateKeyboardDetectionState();
                RefreshCronSchedule();
            }
            return task;
        }
        finally
        {
            CancelExecution(name, cancellation);
        }
    }

    public WinPilotTask SetEnabled(string name, bool enabled) => ChangeEnabled(name, enabled);

    private WinPilotTask ChangeEnabled(string name, bool? enabled)
    {
        lock (gate)
        {
            var task = tasks.TryGetValue(name, out var value) ? value : throw new FileNotFoundException("任务不存在");
            var nextEnabled = enabled ?? !task.Enabled;
            if (task.Enabled == nextEnabled)
            {
                return task;
            }
            var cron = cronExpressions.TryGetValue(name, out var parsed) && ReferenceEquals(parsed.Task, task)
                ? parsed.Expression
                : null;
            task = task with
            {
                Enabled = nextEnabled
            };
            store.Save(task);
            tasks[name] = task;
            ClearCronState(name);
            if (cron is not null)
            {
                cronExpressions[name] = (task, cron);
            }
            UpdateProcessDetectionState();
            UpdateKeyboardDetectionState();
            RefreshCronSchedule();
            return task;
        }
    }

    public void Delete(string name)
    {
        TaskExecution? cancellation = null;
        try
        {
            lock (gate)
            {
                if (!tasks.ContainsKey(name))
                {
                    throw new FileNotFoundException("任务不存在");
                }
                var file = store.GetFilePath(name);
                File.Delete(file);
                tasks.Remove(name);
                cancellation = RequestTaskCancellation(name);
                ClearCronState(name);
                UpdateProcessDetectionState();
                UpdateKeyboardDetectionState();
                RefreshCronSchedule();
            }
        }
        finally
        {
            CancelExecution(name, cancellation);
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
            if (isStopping || token.IsCancellationRequested)
            {
                return;
            }
            serviceToken = token;
            taskServiceStarted = true;
            StartProcessListenerIfNeeded();
            UpdateKeyboardDetectionState();
        }

        logger.LogInformation("任务服务已启动");
        // 插件先完成注册，启动任务及后续调度才能使用扩展动作和变量。
        await pluginsLoaded.Task.WaitAsync(token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), clock, token);
        foreach (var task in GetAll().Where(task => task.Enabled && task.Trigger.Type == TaskTriggerType.WinPilotStartup))
        {
            Signal(task, "WinPilot 启动", token);
        }
        lock (gate)
        {
            cronReady = true;
            RefreshCronSchedule();
        }
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            Task retiring;
            lock (gate)
            {
                cronReady = false;
                cronRetryStartedAt.Clear();
                cronSchedulerRun?.Changed.TrySetResult();
                retiring = cronSchedulerRun?.Execution ?? Task.CompletedTask;
            }
            await retiring.ConfigureAwait(false);
        }
    }

    private void BeginStopping()
    {
        KeyValuePair<string, TaskExecution>[] cancellations;
        lock (gate)
        {
            isStopping = true;
            UpdateKeyboardDetectionState();
            cronRetryStartedAt.Clear();
            taskServiceStarted = false;
            processListenerGeneration++;
            cronSchedulerRun?.Changed.TrySetResult();
            cancellations = RequestAllCancellations();
        }
        CancelExecutions(cancellations);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        KeyValuePair<string, TaskExecution>[] cancellations = [];
        try
        {
            lock (gate)
            {
                isStopping = true;
                UpdateKeyboardDetectionState();
                cronRetryStartedAt.Clear();
                taskServiceStarted = false;
                processListenerGeneration++;
                cronSchedulerRun?.Changed.TrySetResult();
                cancellations = RequestAllCancellations();
                StopProcessListener();
                processPaths.Clear();
                pending = runningTasks.Values.Select(instance => instance.Execution).ToArray();
            }
        }
        finally
        {
            CancelExecutions(cancellations);
        }

        var keyboardStopping = keyboardEvents?.StopAsync(cancellationToken) ?? Task.CompletedTask;
        var stopping = Task.WhenAll(pending.Append(base.StopAsync(cancellationToken)).Append(keyboardStopping));
        _ = stopping.ContinueWith(completed =>
        {
            _ = completed.Exception;
        }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await stopping.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var remaining = pending.Count(task => !task.IsCompleted);
            if (remaining > 0)
            {
                logger.LogWarning("退出等待预算已耗尽，仍有 {Count} 个任务执行未结束，继续退出", remaining);
            }
        }
    }

    public override void Dispose()
    {
        stoppingRegistration.Dispose();
        processEvents.Changed -= ProcessEventsChanged;
        extensions.Triggered -= ExtensionTriggered;
        if (keyboardEvents is not null)
        {
            keyboardEvents.Triggered -= KeyboardTriggered;
        }
        KeyValuePair<string, TaskExecution>[] cancellations = [];
        try
        {
            lock (gate)
            {
                isStopping = true;
                UpdateKeyboardDetectionState();
                cronRetryStartedAt.Clear();
                taskServiceStarted = false;
                processListenerGeneration++;
                cronSchedulerRun?.Changed.TrySetResult();
                cancellations = RequestAllCancellations();
                StopProcessListener();
                processPaths.Clear();
            }
        }
        finally
        {
            CancelExecutions(cancellations);
        }
        keyboardEvents?.Dispose();
        base.Dispose();
    }

    private void UpdateKeyboardDetectionState()
    {
        keyboardEvents?.Update(!taskServiceStarted || isStopping
            ? []
            : tasks.Values
                .Where(task => task.Enabled && task.Trigger.Type == TaskTriggerType.Keyboard)
                .Select(task => new KeyboardTriggerBinding(task, task.Trigger.Keys!.Select(key => Enum.Parse<KeyboardKey>(key, true)).ToArray()))
                .ToArray());
    }

    private void KeyboardTriggered(KeyboardTriggerBinding binding)
    {
        if (binding.Owner is WinPilotTask task)
        {
            _ = TriggerKeyboardTask(task);
        }
    }

    internal Task TriggerKeyboardTask(WinPilotTask task)
    {
        lock (gate)
        {
            if (!taskServiceStarted || isStopping || !task.Enabled || task.Trigger.Type != TaskTriggerType.Keyboard)
            {
                return Task.CompletedTask;
            }
        }
        return RunAsync(task, $"按键监听「{string.Join(" + ", task.Trigger.Keys ?? [])}」", serviceToken, null, dispatch: true);
    }

    private void RefreshCronSchedule()
    {
        if (!cronReady || isStopping)
        {
            return;
        }
        if (cronSchedulerRun is not null)
        {
            cronSchedulerRun.Changed.TrySetResult();
            return;
        }
        if (!tasks.Values.Any(task =>
            task.Enabled
            && task.Trigger.Type == TaskTriggerType.Cron
            && (!nextCron.TryGetValue(task.Name, out var due) || due is not null)))
        {
            return;
        }
        var run = new CronSchedulerRun();
        cronSchedulerRun = run;
        run.Execution = Task.Run(() => RunCronScheduleAsync(run));
        _ = run.Execution.ContinueWith(
            completed => logger.LogError(completed.Exception, "Cron 调度收尾失败"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal void NotifyCronClockChanged()
    {
        if (ReferenceEquals(clock, TimeProvider.System))
        {
            TimeZoneInfo.ClearCachedData();
        }
        lock (gate)
        {
            cronSchedulerRun?.Changed.TrySetResult();
        }
    }

    private async Task RunCronScheduleAsync(CronSchedulerRun run)
    {
        EventHandler timeChanged = (_, _) => NotifyCronClockChanged();
        PowerModeChangedEventHandler powerChanged = (_, args) =>
        {
            if (args.Mode == PowerModes.Resume)
            {
                NotifyCronClockChanged();
            }
        };
        var timeSubscribed = false;
        var powerSubscribed = false;
        try
        {
            if (ReferenceEquals(clock, TimeProvider.System))
            {
                SystemEvents.TimeChanged += timeChanged;
                timeSubscribed = true;
                SystemEvents.PowerModeChanged += powerChanged;
                powerSubscribed = true;
                TimeZoneInfo.ClearCachedData();
            }
            while (true)
            {
                Task changed;
                lock (gate)
                {
                    if (!cronReady
                        || isStopping
                        || serviceToken.IsCancellationRequested
                        || !ReferenceEquals(cronSchedulerRun, run))
                    {
                        return;
                    }
                    if (run.Changed.Task.IsCompleted)
                    {
                        run.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    changed = run.Changed.Task;
                }
                try
                {
                    ProcessDueCronTasks(clock.GetUtcNow(), serviceToken);
                    TimeSpan delay;
                    lock (gate)
                    {
                        if (!cronReady || isStopping || serviceToken.IsCancellationRequested)
                        {
                            return;
                        }
                        if (changed.IsCompleted)
                        {
                            continue;
                        }
                        DateTimeOffset? nearest = null;
                        var unresolved = false;
                        foreach (var task in tasks.Values)
                        {
                            if (!task.Enabled || task.Trigger.Type != TaskTriggerType.Cron)
                            {
                                continue;
                            }
                            if (cronRetryStartedAt.TryGetValue(task.Name, out var startedAt))
                            {
                                var remaining = GetCronRetryRemaining(startedAt);
                                var retry = clock.GetUtcNow() + (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                                if (nearest is null || retry < nearest)
                                {
                                    nearest = retry;
                                }
                                continue;
                            }
                            if (!nextCron.TryGetValue(task.Name, out var due))
                            {
                                unresolved = true;
                            }
                            else if (due is not null && (nearest is null || due < nearest))
                            {
                                nearest = due;
                            }
                        }
                        if (unresolved)
                        {
                            var retry = clock.GetUtcNow().AddSeconds(1);
                            if (nearest is null || retry < nearest)
                            {
                                nearest = retry;
                            }
                        }
                        if (nearest is null)
                        {
                            return;
                        }
                        delay = nearest.Value - clock.GetUtcNow();
                    }
                    if (delay <= TimeSpan.Zero)
                    {
                        continue;
                    }
                    delay = TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling(delay.TotalMilliseconds), 1d, uint.MaxValue - 1d));
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
                    try
                    {
                        await Task.WhenAny(changed, Task.Delay(delay, clock, waiting.Token)).ConfigureAwait(false);
                    }
                    finally
                    {
                        await waiting.CancelAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "任务监听失败");
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, serviceToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cron 调度异常结束");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, serviceToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            try
            {
                try
                {
                    if (powerSubscribed)
                    {
                        SystemEvents.PowerModeChanged -= powerChanged;
                    }
                }
                finally
                {
                    if (timeSubscribed)
                    {
                        SystemEvents.TimeChanged -= timeChanged;
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(cronSchedulerRun, run))
                    {
                        cronSchedulerRun = null;
                        RefreshCronSchedule();
                    }
                }
            }
        }
    }

    internal void ProcessDueCronTasks(DateTimeOffset now, CancellationToken token)
    {
        WinPilotTask[] scheduledTasks;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (isStopping)
            {
                return;
            }
            var timeZone = clock.LocalTimeZone;
            if (!cronTimeZone.Equals(timeZone))
            {
                foreach (var name in nextCron.Where(pair => pair.Value is not null).Select(pair => pair.Key).ToArray())
                {
                    nextCron.Remove(name);
                }
                cronTimeZone = timeZone;
            }
            scheduledTasks = tasks.Values
                .Where(task => task.Enabled && task.Trigger.Type == TaskTriggerType.Cron)
                .OrderBy(task => task.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        foreach (var task in scheduledTasks)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var exhausted = false;
                DateTimeOffset? scheduledAt = null;
                lock (gate)
                {
                    if (isStopping || !tasks.TryGetValue(task.Name, out var current) || !ReferenceEquals(current, task))
                    {
                        continue;
                    }
                    token.ThrowIfCancellationRequested();
                    if (cronRetryStartedAt.TryGetValue(task.Name, out var startedAt)
                        && GetCronRetryRemaining(startedAt) > TimeSpan.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        if (!nextCron.TryGetValue(task.Name, out var due))
                        {
                            nextCron[task.Name] = due = GetNextCronOccurrence(task, now);
                            exhausted = due is null;
                        }
                        if (due is not null && now >= due.Value)
                        {
                            nextCron[task.Name] = GetNextCronOccurrence(task, now);
                            exhausted = nextCron[task.Name] is null;
                            scheduledAt = due.Value;
                        }
                        cronRetryStartedAt.Remove(task.Name);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        cronRetryStartedAt[task.Name] = clock.GetTimestamp();
                        throw;
                    }
                }
                if (exhausted)
                {
                    logger.LogWarning("任务「{Name}」Cron 表达式没有后续执行时间，已停止该任务调度：{Cron}", task.Name, task.Trigger.Cron);
                }
                if (scheduledAt is not null)
                {
                    _ = RunAsync(task, $"定时 {task.Trigger.Cron}", token, null, dispatch: true, cronScheduledAt: scheduledAt);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "任务「{Name}」Cron 调度失败，表达式：{Cron}", task.Name, task.Trigger.Cron);
            }
        }
    }

    private DateTimeOffset? GetNextCronOccurrence(WinPilotTask task, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        if (!cronExpressions.TryGetValue(task.Name, out var parsed) || !ReferenceEquals(parsed.Task, task))
        {
            parsed = (task, TaskCronExpression.Parse(task.Trigger.Cron ?? throw new InvalidOperationException("Cron 表达式不能为空")));
            cronExpressions[task.Name] = parsed;
        }
        return parsed.Expression.GetNextOccurrence(now, timeZone ?? cronTimeZone);
    }

    private TimeSpan GetCronRetryRemaining(long startedAt) => TimeSpan.FromSeconds(1) - clock.GetElapsedTime(startedAt);

    private void ClearCronState(string name)
    {
        nextCron.Remove(name);
        cronExpressions.Remove(name);
        cronRetryStartedAt.Remove(name);
    }

    private void ProcessEventsChanged(object? sender, WindowsProcessEvent processEvent) =>
        ProcessEvent(processEvent, GetProcessPath);

    internal void ProcessEvent(WindowsProcessEvent processEvent, Func<int, string> resolveProcessPath)
    {
        try
        {
            IReadOnlyList<WinPilotTask> processTasks;
            IReadOnlyList<WinPilotTask> allProcessTasks;
            long generation;
            var expectedEvent = processEvent.Type == WindowsProcessEventType.Started
                ? TaskProcessEventType.Started
                : TaskProcessEventType.Exited;
            lock (gate)
            {
                if (isStopping || !taskServiceStarted || !processDetectionEnabled || !processListenerStarted)
                {
                    return;
                }
                generation = processListenerGeneration;
                allProcessTasks = tasks.Values.Where(task => task.Enabled && IsProcessTrigger(task.Trigger)).ToArray();
                processTasks = allProcessTasks.Where(task => task.Trigger.Event == expectedEvent).ToArray();
            }

            var cachePaths = allProcessTasks
                .SelectMany(task => task.Trigger.ProcessPaths ?? [])
                .Where(path => Path.GetFileNameWithoutExtension(path).Equals(processEvent.ProcessName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var processPath = "";
            if (processEvent.Type == WindowsProcessEventType.Started && cachePaths.Length > 0)
            {
                processPath = resolveProcessPath(processEvent.ProcessId);
            }
            lock (gate)
            {
                if (isStopping
                    || !taskServiceStarted
                    || !processDetectionEnabled
                    || !processListenerStarted
                    || generation != processListenerGeneration)
                {
                    return;
                }
                if (processEvent.Type == WindowsProcessEventType.Started && cachePaths.Length > 0)
                {
                    processPaths[processEvent.ProcessId] = processPath;
                }
                if (processEvent.Type == WindowsProcessEventType.Exited
                    && processPaths.Remove(processEvent.ProcessId, out var known))
                {
                    processPath = known;
                }
            }

            foreach (var task in processTasks)
            {
                if (!Matches(task.Trigger.ProcessPaths, processEvent.ProcessName, processPath))
                {
                    continue;
                }
                Signal(task, expectedEvent == TaskProcessEventType.Started ? "当程序启动时" : "当程序退出时", serviceToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "处理进程事件失败，进程 ID：{ProcessId}，进程名称：{ProcessName}，事件类型：{Type}",
                processEvent.ProcessId,
                processEvent.ProcessName,
                processEvent.Type);
        }
    }

    /// <summary>
    /// 根据已启用的程序触发任务决定是否保留系统进程监听，不随页面访问常驻。
    /// </summary>
    private void UpdateProcessDetectionState()
    {
        var enabled = tasks.Values.Any(task => task.Enabled && IsProcessTrigger(task.Trigger));
        if (enabled == processDetectionEnabled)
        {
            if (enabled && taskServiceStarted)
            {
                PrimeProcessPaths();
            }
            return;
        }

        processDetectionEnabled = enabled;
        processListenerGeneration++;
        processPaths.Clear();
        if (!taskServiceStarted)
        {
            return;
        }
        if (enabled)
        {
            StartProcessListenerIfNeeded();
        }
        else
        {
            StopProcessListener();
        }
    }

    private void StartProcessListenerIfNeeded()
    {
        if (!processDetectionEnabled || processListenerStarted)
        {
            return;
        }
        if (!processEvents.Start())
        {
            return;
        }
        processListenerGeneration++;
        processListenerStarted = true;
        PrimeProcessPaths();
        logger.LogInformation("任务触发所需的 Windows 进程监听已启动");
    }

    private void StopProcessListener()
    {
        if (!processListenerStarted)
        {
            return;
        }
        processListenerGeneration++;
        processEvents.Stop();
        processListenerStarted = false;
        processPaths.Clear();
        logger.LogInformation("任务触发所需的 Windows 进程监听已停止");
    }

    private void PrimeProcessPaths()
    {
        var names = tasks.Values
            .Where(task => task.Enabled && IsProcessTrigger(task.Trigger))
            .SelectMany(task => task.Trigger.ProcessPaths ?? [])
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = SafePath(process);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        processPaths[process.Id] = path;
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private static bool IsProcessTrigger(TaskTrigger trigger) => trigger.Type == TaskTriggerType.Process;

    private static string GetProcessPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return SafePath(process);
        }
        catch
        {
            return "";
        }
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

    internal static bool Matches(IReadOnlyList<string>? paths, string name, string path) =>
        paths?.Any(expected =>
            Path.GetFileNameWithoutExtension(expected).Equals(name, StringComparison.OrdinalIgnoreCase)
            && expected.Equals(path, StringComparison.OrdinalIgnoreCase)) == true;

    private void Signal(WinPilotTask task, string reason, CancellationToken token) =>
        _ = RunAsync(task, reason, token, null);

    private void ExtensionTriggered(PluginTaskTriggerFired fired)
    {
        try
        {
            if (!extensions.IsCurrent(fired))
            {
                return;
            }
            WinPilotTask[] matches;
            lock (gate)
            {
                matches = tasks.Values
                    .Where(task =>
                        task.Enabled
                        && task.Trigger.Type == TaskTriggerType.Extension
                        && task.Trigger.PluginId?.Equals(fired.PluginId, StringComparison.OrdinalIgnoreCase) == true
                        && task.Trigger.TriggerId?.Equals(fired.TriggerId, StringComparison.OrdinalIgnoreCase) == true
                        && MatchesTriggerParameters(task.Trigger.Parameters, fired.Variables))
                    .ToArray();
            }
            foreach (var task in matches)
            {
                _ = RunAsync(task, $"扩展触发 {fired.TriggerId}", fired.CancellationToken, fired);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "处理插件任务触发失败，插件 ID：{PluginId}，触发器 ID：{TriggerId}", fired.PluginId, fired.TriggerId);
        }
    }

    private static bool MatchesTriggerParameters(
        IReadOnlyDictionary<string, object?>? expected,
        IReadOnlyDictionary<string, object?> actual) =>
        expected is null
        || expected.All(item =>
            actual.TryGetValue(item.Key, out var value)
            && string.Equals(Convert.ToString(item.Value), Convert.ToString(value), StringComparison.Ordinal));

    /// <summary>
    /// 所有触发入口共用的执行登记；忽略过期定义、停止中的请求和已在运行的同名任务。
    /// </summary>
    private Task RunAsync(
        WinPilotTask task,
        string reason,
        CancellationToken token,
        PluginTaskTriggerFired? fired,
        bool dispatch = false,
        DateTimeOffset? cronScheduledAt = null)
    {
        TaskExecution? instance = null;
        var reportSkipped = false;
        var canReportSkipped = cronScheduledAt is not null && logger.IsEnabled(LogLevel.Debug);
        lock (gate)
        {
            if (isStopping
                || token.IsCancellationRequested
                || !tasks.TryGetValue(task.Name, out var current)
                || !ReferenceEquals(current, task))
            {
                return Task.CompletedTask;
            }
            if (runningTasks.TryGetValue(task.Name, out var running))
            {
                if (canReportSkipped && !running.CronSkipReported)
                {
                    running.CronSkipReported = true;
                    reportSkipped = true;
                }
            }
            else
            {
                instance = new(CancellationTokenSource.CreateLinkedTokenSource(token));
                runningTasks.Add(task.Name, instance);
            }
        }
        if (instance is null)
        {
            if (reportSkipped)
            {
                logger.LogDebug("任务「{Name}」仍在运行，已跳过计划时间 {ScheduledAt:O} 的 Cron 触发；本次执行期间不再重复提示", task.Name, cronScheduledAt);
            }
            return Task.CompletedTask;
        }
        instance.Started.TrySetResult(dispatch
            ? Task.Run(() => ExecuteTaskAsync(task, reason, fired, instance))
            : ExecuteTaskAsync(task, reason, fired, instance));
        _ = instance.Execution.ContinueWith(
            completed => logger.LogError(completed.Exception, "任务「{Name}」执行异常结束", task.Name),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return instance.Execution;
    }

    private async Task ExecuteTaskAsync(
        WinPilotTask task,
        string reason,
        PluginTaskTriggerFired? fired,
        TaskExecution execution)
    {
        var instance = execution.Cancellation;
        try
        {
            var timestamp = fired?.Timestamp ?? DateTimeOffset.Now;
            var variables = new Dictionary<string, object?>(fired?.Variables ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
            variables["$time"] = $"[{timestamp:yyyy-MM-dd HH:mm:ss}]";
            var context = new PluginTaskRunContext(
                task.Name,
                fired?.PluginId,
                fired?.TriggerId,
                timestamp,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(variables),
                instance.Token);
            logger.LogInformation("任务「{Name}」开始执行，触发：{Reason}", task.Name, reason);
            var failedCount = 0;
            var stoppedAfterFailure = false;
            // 每个动作执行前检查取消；失败后是否继续仅由当前动作的配置决定。
            for (var index = 0; index < task.Actions.Count; index++)
            {
                var action = task.Actions[index];
                var description = DescribeAction(action);
                try
                {
                    logger.LogInformation("任务「{Name}」第 {Index} 个动作开始：{Action}", task.Name, index + 1, description);
                    if (execution.CancellationRequested)
                    {
                        throw new OperationCanceledException(instance.Token);
                    }
                    instance.Token.ThrowIfCancellationRequested();
                    await Execute(action, context);
                }
                catch (OperationCanceledException) when (execution.CancellationRequested || instance.IsCancellationRequested)
                {
                    logger.LogInformation("任务「{Name}」已取消", task.Name);
                    return;
                }
                catch (Exception exception)
                {
                    failedCount++;
                    logger.LogError(
                        exception,
                        action.ContinueOnFailure
                            ? "任务「{Name}」第 {Index} 个动作失败：{Action}，继续执行后续动作"
                            : "任务「{Name}」第 {Index} 个动作失败：{Action}，停止执行后续动作",
                        task.Name,
                        index + 1,
                        description);
                    if (!action.ContinueOnFailure)
                    {
                        stoppedAfterFailure = true;
                        break;
                    }
                }
            }

            if (failedCount == 0)
            {
                logger.LogInformation("任务「{Name}」执行完成", task.Name);
            }
            else if (stoppedAfterFailure)
            {
                logger.LogWarning("任务「{Name}」执行结束，共 {Count} 个动作失败，后续动作未执行", task.Name, failedCount);
            }
            else
            {
                logger.LogWarning("任务「{Name}」执行完成，共 {Count} 个动作失败", task.Name, failedCount);
            }
        }
        finally
        {
            CancellationTokenSource? completed;
            lock (gate)
            {
                completed = execution.CompleteExecution();
                if (runningTasks.TryGetValue(task.Name, out var current) && ReferenceEquals(current, execution))
                {
                    runningTasks.Remove(task.Name);
                }
            }
            completed?.Dispose();
        }
    }

    private async Task Execute(TaskAction action, PluginTaskRunContext context)
    {
        var token = context.CancellationToken;
        switch (action.Type)
        {
            case TaskActionType.StartProgram:
                programs.Launch(new(
                    action.ProgramPath ?? "",
                    action.Arguments ?? "",
                    action.WorkingDirectory ?? "",
                    action.RunElevated,
                    action.Silent));
                break;
            case TaskActionType.StartTag:
                var tag = tags.Get(action.TagName ?? "");
                programs.Launch(new(tag.ProgramPath, tag.Arguments, tag.WorkingDirectory, tag.RunElevated, tag.Silent));
                break;
            case TaskActionType.EndProcess:
                var ids = GetEndProcessIds(action.ProcessName, action.CurrentUserOnly);
                if (ids.Length == 0)
                {
                    throw new InvalidOperationException("未找到目标进程");
                }
                programs.Close(ids, isGroup: false);
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
                await notificationSender.SendAsync(action, context);
                break;
            case TaskActionType.Extension:
                await ExecuteExtension(action, context);
                break;
            case TaskActionType.TaskState:
                ChangeEnabled(
                    action.TaskName ?? "",
                    action.State switch
                    {
                        TaskStateOperation.Toggle => null,
                        TaskStateOperation.Enable => true,
                        TaskStateOperation.Disable => false,
                        _ => throw new InvalidOperationException("任务状态无效")
                    });
                break;
        }
    }

    private string DescribeAction(TaskAction action) =>
        action.Type switch
        {
            TaskActionType.StartProgram => $"启动程序「{action.ProgramPath}」",
            TaskActionType.StartTag => $"启动快捷标签「{action.TagName}」",
            TaskActionType.EndProcess => $"结束进程「{action.ProcessName}」",
            TaskActionType.Delay => $"延迟 {action.DelaySeconds} 秒",
            TaskActionType.Keyboard => $"发送按键「{string.Join(" + ", action.Keys ?? [])}」",
            TaskActionType.Shell => $"Shell「{action.Shell}」执行命令",
            TaskActionType.SendNotification => string.IsNullOrWhiteSpace(action.NotificationChannel)
                ? "发送通知（未选择渠道）"
                : $"发送「{NotificationChannelCatalog.GetDisplayName(action.NotificationChannel)}」通知",
            TaskActionType.Extension when action.PluginId is not null
                && action.ActionId is not null
                && extensions.Actions.TryGet(action.PluginId, action.ActionId, out var definition) => $"插件扩展「{definition.PluginDisplayName}」",
            TaskActionType.Extension => $"插件扩展「{action.PluginId}」",
            TaskActionType.TaskState => $"{(action.State == TaskStateOperation.Toggle
                ? "切换"
                : action.State == TaskStateOperation.Enable ? "启用" : "禁用")}任务「{action.TaskName}」",
            _ => action.Type.ToString()
        };

    private int[] GetEndProcessIds(string? name, bool currentUserOnly)
    {
        var processName = Path.GetFileNameWithoutExtension(name ?? "");
        if (currentUserOnly)
        {
            using var identity = WindowsIdentity.GetCurrent();
            return programs
                .GetRunningPrograms(identity.Name)
                .Where(program =>
                    string.Equals(Path.GetFileNameWithoutExtension(program.Name), processName, StringComparison.OrdinalIgnoreCase))
                .Select(program => program.ProcessId)
                .ToArray();
        }
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Select(process => process.Id).ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private TaskExecution? RequestTaskCancellation(string name)
    {
        return runningTasks.TryGetValue(name, out var instance) && instance.TryRequestCancellation()
            ? instance
            : null;
    }

    private KeyValuePair<string, TaskExecution>[] RequestAllCancellations() =>
        runningTasks.Where(task => task.Value.TryRequestCancellation()).ToArray();

    private void CancelExecution(string name, TaskExecution? instance)
    {
        if (instance is null)
        {
            return;
        }
        try
        {
            instance.Cancellation.Cancel();
        }
        catch (AggregateException exception)
        {
            logger.LogWarning(exception, "取消任务「{Name}」时，取消回调失败", name);
        }
        finally
        {
            CancellationTokenSource? completed;
            lock (gate)
            {
                completed = instance.CompleteCancellation();
            }
            completed?.Dispose();
        }
    }

    private void CancelExecutions(KeyValuePair<string, TaskExecution>[] cancellations)
    {
        foreach (var task in cancellations)
        {
            CancelExecution(task.Key, task.Value);
        }
    }

    private async Task ExecuteExtension(TaskAction action, PluginTaskRunContext context)
    {
        if (string.IsNullOrWhiteSpace(action.PluginId)
            || string.IsNullOrWhiteSpace(action.ActionId)
            || !extensions.Actions.TryGet(action.PluginId, action.ActionId, out var definition))
        {
            throw new InvalidOperationException("扩展动作不可用");
        }
        var parameters = new Dictionary<string, object?>(action.Parameters ?? new Dictionary<string, object?>());
        var schema = definition.Parameters;
        if (definition.OptionsResolver is not null)
        {
            var current = new List<PluginActionParameterDefinition>(schema.Count);
            foreach (var parameter in schema)
            {
                var options = parameter.Type is PluginActionParameterType.Select or PluginActionParameterType.MultiCheckbox
                    ? await extensions.GetActionOptionsAsync(action.PluginId, action.ActionId, parameter.Key, context.CancellationToken)
                    : parameter.Options;
                current.Add(parameter with { Options = options });
            }

            schema = current;
        }

        foreach (var parameter in schema)
        {
            if (!parameters.ContainsKey(parameter.Key) && parameter.DefaultValue is not null)
            {
                parameters.Add(parameter.Key, CloneActionDefault(parameter.DefaultValue));
            }
        }
        ValidateActionParameters(schema, parameters);
        if (definition.HostActionPlan is not null)
        {
            foreach (var step in definition.HostActionPlan.Steps)
            {
                await ExecuteHostStep(step, parameters, context);
            }
            return;
        }

        if (definition.Handler is null)
        {
            throw new InvalidOperationException("扩展动作不可用");
        }
        var result = await definition.Handler(
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(parameters)),
            context,
            context.CancellationToken);
        if (result.Status != PluginTaskActionResultStatus.Success)
        {
            throw new InvalidOperationException(result.Error ?? result.Status.ToString());
        }
        if (result.HostActionPlan is not null)
        {
            PluginTaskActionRegistry.ValidatePlan(result.HostActionPlan);
            foreach (var step in result.HostActionPlan.Steps)
            {
                await ExecuteHostStep(step, parameters, context);
            }
        }
    }

    private async Task ExecuteHostStep(
        PluginHostActionStep step,
        IReadOnlyDictionary<string, object?> extensionParameters,
        PluginTaskRunContext context)
    {
        var values = step.Parameters.ToDictionary(pair => pair.Key, pair => ResolvePlanValue(pair.Value, extensionParameters));
        var (action, image) = PluginHostActionConverter.Convert(step with { Parameters = values }, defaultSilent: false, defaultShell: null);
        if (step.Type == PluginHostActionType.SendNotification)
        {
            await notificationSender.SendAsync(action, context, image);
            return;
        }

        await Execute(action, context);
    }

    private static object? ResolvePlanValue(object? value, IReadOnlyDictionary<string, object?> parameters) =>
        value is string text
        && text.StartsWith("$param.", StringComparison.Ordinal)
        && parameters.TryGetValue(text[7..], out var resolved)
            ? resolved
            : value;

    private static object? CloneActionDefault(object? value) =>
        value switch
        {
            JsonElement element => element.Clone(),
            System.Text.Json.Nodes.JsonNode node => node.DeepClone(),
            IEnumerable<string> items => items.ToArray(),
            IEnumerable<object?> items => items.Select(CloneActionDefault).ToArray(),
            Array array => array.Clone(),
            _ => value
        };

    private static void ValidateActionParameters(
        IReadOnlyList<PluginActionParameterDefinition> schema,
        IReadOnlyDictionary<string, object?> values)
    {
        foreach (var value in values.Keys.Where(key => !schema.Any(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException($"未知动作参数：{value}");
        }
        foreach (var parameter in schema)
        {
            values.TryGetValue(parameter.Key, out var value);
            var selected = ParameterStrings(value);
            if (parameter.Required
                && (value is null
                    || (parameter.Type == PluginActionParameterType.MultiCheckbox
                        ? selected.Length == 0
                        : string.IsNullOrWhiteSpace(Convert.ToString(value)))))
            {
                throw new InvalidOperationException($"缺少动作参数：{parameter.Key}");
            }
            if (value is not null
                && parameter.Type == PluginActionParameterType.Number
                && !decimal.TryParse(Convert.ToString(value), out _))
            {
                throw new InvalidOperationException($"动作参数类型无效：{parameter.Key}");
            }
            if (value is not null
                && parameter.Type == PluginActionParameterType.Select
                && parameter.Options?.All(option => !option.Value.Equals(Convert.ToString(value), StringComparison.Ordinal)) == true)
            {
                throw new InvalidOperationException($"动作参数选项无效：{parameter.Key}");
            }
            if (value is not null && parameter.Type == PluginActionParameterType.MultiCheckbox)
            {
                var empty = value is JsonElement { ValueKind: JsonValueKind.Array } array
                    ? array.GetArrayLength() == 0
                    : value is IEnumerable<object?> items && !items.Any();
                if (selected.Length == 0
                    && !empty
                    || selected.Any(item => parameter.Options?.All(option => !option.Value.Equals(item, StringComparison.Ordinal)) != false))
                {
                    throw new InvalidOperationException($"动作参数选项无效：{parameter.Key}");
                }
            }
        }
    }

    private static string[] ParameterStrings(object? value)
    {
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray();
        }
        return value is IEnumerable<object?> items
            ? items.Select(Convert.ToString).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray()
            : [];
    }

    private void Load()
    {
        foreach (var file in store.EnumerateFiles())
        {
            try
            {
                var task = TaskDefinitionValidator.Normalize(store.Read(file));
                var cron = TaskDefinitionValidator.Validate(task);
                if (!string.Equals(Path.GetFileNameWithoutExtension(file), task.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("任务名称与文件名不一致");
                }
                if (!tasks.TryAdd(task.Name, task))
                {
                    throw new InvalidDataException("任务名称重复");
                }
                if (cron is not null)
                {
                    cronExpressions[task.Name] = (task, cron);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "无法读取任务文件 {File}", file);
            }
        }
        UpdateProcessDetectionState();
    }

    private sealed class CronSchedulerRun
    {
        public TaskCompletionSource Changed { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Execution { get; set; } = Task.CompletedTask;
    }

    private sealed class TaskExecution
    {
        private bool cancellationRequested;
        private bool cancellationInProgress;
        private bool executionCompleted;
        private bool cancellationDisposed;

        internal bool CancellationRequested => Volatile.Read(ref cancellationRequested);
        internal bool CronSkipReported { get; set; }
        internal CancellationTokenSource Cancellation { get; }
        internal TaskCompletionSource<Task> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Execution { get; }

        internal TaskExecution(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
            Execution = Started.Task.Unwrap();
        }

        internal bool TryRequestCancellation()
        {
            if (cancellationRequested || executionCompleted)
            {
                return false;
            }
            Volatile.Write(ref cancellationRequested, true);
            cancellationInProgress = true;
            return true;
        }

        internal CancellationTokenSource? CompleteExecution()
        {
            executionCompleted = true;
            return TakeCompletedCancellation();
        }

        internal CancellationTokenSource? CompleteCancellation()
        {
            cancellationInProgress = false;
            return TakeCompletedCancellation();
        }

        private CancellationTokenSource? TakeCompletedCancellation()
        {
            if (!executionCompleted || cancellationInProgress || cancellationDisposed)
            {
                return null;
            }
            cancellationDisposed = true;
            return Cancellation;
        }
    }
}
