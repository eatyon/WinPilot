using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WinPilot.Core;
using WinPilot.Features.Desktop;
using WinPilot.Core.SystemStatus;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;
using WinPilot.Features.Tasks;
using WinPilot.Features.Plugins;

namespace WinPilot.Features.Notifications.CommandBots;

public sealed record CommandBotStatus(bool Enabled, string State, string? Error = null);
public sealed record CommandBotMessage(string ChatId, string Sender, string Content)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? CommandText { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? ConversationType { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? MessageId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal long? ThreadId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal Func<string, byte[]?, CancellationToken, Task>? Reply { get; init; }
}
public sealed record CommandBotOptions(bool Enabled, string Prefix = "");

public abstract class CommandBotService : IHostedService, IDisposable, IAsyncDisposable
{
    private readonly PortableDataPaths paths;
    private readonly CommandBotConfiguration configuration;
    private readonly ILogger logger;
    private readonly string name;
    private readonly DesktopScreenshotService? screenshots;
    private readonly ISystemResourceMonitor? systemMonitor;
    private readonly ProgramTagService? tags;
    private readonly IProgramManager? programs;
    private readonly TaskService? tasks;
    private readonly PluginCommandRegistry? pluginCommands;
    protected CommandBotService(PortableDataPaths paths, CommandBotConfiguration configuration, ILogger logger, string name,
        DesktopScreenshotService? screenshots = null, ISystemResourceMonitor? systemMonitor = null,
        ProgramTagService? tags = null, IProgramManager? programs = null, TaskService? tasks = null, PluginCommandRegistry? pluginCommands = null)
    {
        this.paths = paths; this.configuration = configuration; this.logger = logger; this.name = name;
        this.screenshots = screenshots;
        this.systemMonitor = systemMonitor;
        this.tags = tags;
        this.programs = programs;
        this.tasks = tasks;
        this.pluginCommands = pluginCommands;
        configuration.Register(this);
    }
    protected abstract bool IsEnabled(CommandBotSettings settings);
    protected abstract CommandBotSettings WithEnabled(CommandBotSettings settings, bool enabled);
    protected virtual string GetPrefix(CommandBotSettings settings) => "";
    protected virtual CommandBotSettings WithPrefix(CommandBotSettings settings, string prefix) => settings;
    protected virtual bool SameBotConnection(CommandBotSettings a, CommandBotSettings b) => IsEnabled(a) == IsEnabled(b);
    private string activePrefix = "";
    protected virtual void ValidateSettings(NotificationSettings settings) { }
    protected virtual void ValidateConfiguration(WinPilotSettings settings) => ValidateSettings(settings.Notifications ?? new());
    protected virtual bool SameConnection(NotificationSettings a, NotificationSettings b) => true;
    private protected abstract CommandBotReceiver CreateReceiver();
    internal bool ConnectionChanged(NotificationSettings a, NotificationSettings b, CommandBotSettings bots) => IsEnabled(bots) && !SameConnection(a, b);
    internal void ValidateConnection(NotificationSettings settings) => ValidateSettings(settings);
    internal Task RefreshAsync() => StartAsync(CancellationToken.None);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object gate = new();
    private readonly HashSet<Channel<CommandBotStatus>> subscribers = [];
    private CommandBotStatus status = new(false, "disabled");
    private CancellationTokenSource? connectionCancellation;
    private Task? connection;
    private Channel<(CommandBotMessage Message, string Prefix)>? commands;
    private bool stopping;
    private bool disposed;
    private Task? disposal;

    public CommandBotStatus Status { get { lock (gate) return status; } }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try { ObjectDisposedException.ThrowIf(disposed, this); await RestartCoreAsync(); }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            stopping = true;
            await StopConnectionAsync();
            SetStatus(new(false, "disabled"));
        }
        finally { lifecycle.Release(); }
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        SaveCommandBotSettingsAsync(settings => WithEnabled(settings, enabled), cancellationToken);

    public CommandBotOptions GetSettings()
    {
        var settings = WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).CommandBots;
        return new(IsEnabled(settings), GetPrefix(settings));
    }

    public Task SaveSettingsAsync(CommandBotOptions options, CancellationToken token) =>
        SaveCommandBotSettingsAsync(settings => WithPrefix(WithEnabled(settings, options.Enabled), options.Prefix), token);

    internal static void ValidatePrefix(string prefix)
    {
        if (prefix is null || prefix.Any(char.IsWhiteSpace) || prefix.Any(char.IsControl))
            throw new InvalidOperationException("命令前缀不能包含空白或控制字符；不使用前缀请留空");
    }

    protected async Task SaveCommandBotSettingsAsync(Func<CommandBotSettings, CommandBotSettings> update, CancellationToken cancellationToken)
    {
        await configuration.Gate.WaitAsync(cancellationToken);
        try
        {
            await lifecycle.WaitAsync(CancellationToken.None);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                CommandBotSettings previous = new();
                var saved = WinPilotSettings.Update(paths.ConfigurationFile, current =>
                {
                    previous = current.CommandBots;
                    var settings = update(previous);
                    ValidatePrefix(GetPrefix(settings));
                    var next = current with { CommandBots = settings };
                    if (IsEnabled(settings)) ValidateConfiguration(next);
                    return next;
                });
                var settings = saved.CommandBots;
                Volatile.Write(ref activePrefix, GetPrefix(settings));
                if (!SameBotConnection(previous, settings)) await RestartCoreAsync();
                if (IsEnabled(previous) != IsEnabled(settings))
                    logger.LogInformation("{Channel} 命令机器人已{State}", name, IsEnabled(settings) ? "开启" : "关闭");
                else logger.LogInformation("{Channel} 命令机器人设置已保存", name);
            }
            finally { lifecycle.Release(); }
        }
        finally { configuration.Gate.Release(); }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        await configuration.Gate.WaitAsync(cancellationToken);
        try
        {
            await lifecycle.WaitAsync(CancellationToken.None);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!IsEnabled(WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).CommandBots))
                    throw new InvalidOperationException("请先开启命令机器人");
                await RestartCoreAsync();
            }
            finally { lifecycle.Release(); }
        }
        finally { configuration.Gate.Release(); }
    }

    private async Task RestartCoreAsync()
    {
        await StopConnectionAsync();
        if (stopping) return;
        var current = WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile);
        if (!IsEnabled(current.CommandBots)) { SetStatus(new(false, "disabled")); return; }
        try { ValidateConfiguration(current); ValidatePrefix(GetPrefix(current.CommandBots)); }
        catch (InvalidOperationException exception) { SetStatus(new(true, "failed", exception.Message)); return; }
        SetStatus(new(true, "connecting"));
        Volatile.Write(ref activePrefix, GetPrefix(current.CommandBots));
        connectionCancellation = new();
        var token = connectionCancellation.Token;
        var activeReceiver = CreateReceiver();
        var queue = Channel.CreateBounded<(CommandBotMessage, string)>(new BoundedChannelOptions(16)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        Volatile.Write(ref commands, queue);
        connection = Task.Run(async () =>
        {
            using var processing = CancellationTokenSource.CreateLinkedTokenSource(token);
            async Task ProcessCommandsAsync()
            {
                Commands.CommandProcessor? processor = null;
                try
                {
                    await foreach (var item in queue.Reader.ReadAllAsync(processing.Token))
                    {
                        processing.Token.ThrowIfCancellationRequested();
                        processor ??= new(name, current.Notifications, logger, screenshots, systemMonitor, tags, programs, tasks, pluginCommands);
                        await processor.ProcessAsync(item.Item1, item.Item2, processing.Token);
                    }
                }
                catch (OperationCanceledException) when (processing.IsCancellationRequested) { }
                finally { processor?.Dispose(); }
            }
            var worker = ProcessCommandsAsync();
            try { await activeReceiver.RunAsync(current, token); }
            finally
            {
                Interlocked.CompareExchange(ref commands, null, queue);
                queue.Writer.TryComplete();
                processing.Cancel();
                try { await worker; }
                finally { while (queue.Reader.TryRead(out _)) { } }
            }
        });
    }

    private async Task StopConnectionAsync()
    {
        if (connectionCancellation is null) return;
        var cancellation = connectionCancellation;
        try
        {
            try { cancellation.Cancel(); }
            catch (AggregateException) { logger.LogWarning("{Channel} 命令机器人停止接收消息时发生异常，正在等待接收任务结束", name); }
            if (connection is not null) await connection.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            logger.LogWarning("{Channel} 命令机器人的消息接收任务异常结束，将清理连接状态", name);
        }
        finally
        {
            cancellation.Dispose();
            connectionCancellation = null;
            connection = null;
        }
    }

    protected void Receive(CommandBotMessage message)
    {
        var queue = Volatile.Read(ref commands);
        if (queue is null) return;
        var command = ExtractCommand(message.CommandText ?? message.Content, Volatile.Read(ref activePrefix));
        if (command is null) return;
        var commandName = Commands.CommandProcessor.GetCommandName(command) ?? pluginCommands?.GetCommandName(command);
        if (commandName is null) return;
        message = message with { Content = command };
        logger.LogDebug("{Channel} 命令机器人收到消息：{Command}", name,
            Commands.CommandProcessor.FormatCommandForLog(commandName, command));
        if (!queue.Writer.TryWrite((message, Volatile.Read(ref activePrefix))) && ReferenceEquals(Volatile.Read(ref commands), queue))
            logger.LogWarning("{Channel} 命令处理队列已满，本次命令未处理", name);
    }

    internal static string? ExtractCommand(string text, string prefix)
    {
        var content = text.TrimStart();
        if (!content.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var command = content[prefix.Length..].Trim();
        return command.Length == 0 ? null : command;
    }

    internal static string StripLeadingBotMention(string text, string? mention, bool requireSeparator = true)
    {
        var content = text.TrimStart();
        if (string.IsNullOrEmpty(mention)) return content;
        while (content.StartsWith(mention, StringComparison.Ordinal) &&
            (!requireSeparator || content.Length == mention.Length || char.IsWhiteSpace(content[mention.Length])))
            content = content[mention.Length..].TrimStart();
        return content;
    }

    protected void SetStatus(CommandBotStatus value)
    {
        bool connected;
        bool failed;
        lock (gate)
        {
            if (status == value) return;
            connected = value.State == "connected" && status.State != "connected";
            failed = value.State == "failed" && status.State != "failed";
            status = value;
            foreach (var subscriber in subscribers) subscriber.Writer.TryWrite(value);
        }
        if (connected) logger.LogInformation("{Channel} 命令机器人连接成功", name);
        if (failed) logger.LogWarning("{Channel} 命令机器人连接失败，已停止接收：{Reason}", name, value.Error ?? "未知原因");
    }

    public async IAsyncEnumerable<CommandBotStatus> WatchAsync([EnumeratorCancellation] CancellationToken token)
    {
        var channel = Channel.CreateBounded<CommandBotStatus>(new BoundedChannelOptions(1)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        lock (gate)
        {
            if (disposal is not null) channel.Writer.TryComplete();
            else { subscribers.Add(channel); channel.Writer.TryWrite(status); }
        }
        try { await foreach (var value in channel.Reader.ReadAllAsync(token)) yield return value; }
        finally { lock (gate) subscribers.Remove(channel); channel.Writer.TryComplete(); }
    }

    public void Dispose() => GetDisposalTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(GetDisposalTask());

    private Task GetDisposalTask()
    {
        lock (gate) return disposal ??= DisposeCoreAsync();
    }

    private async Task DisposeCoreAsync()
    {
        await configuration.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                stopping = true;
                await StopConnectionAsync().ConfigureAwait(false);
                SetStatus(new(false, "disabled"));
                configuration.Unregister(this);
                lock (gate)
                {
                    foreach (var subscriber in subscribers) subscriber.Writer.TryComplete();
                    subscribers.Clear();
                    disposed = true;
                }
            }
            finally { lifecycle.Release(); }
        }
        finally { configuration.Gate.Release(); }
    }

}
