using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WinPilot.Core;
using WinPilot.Features.Terminal;

namespace WinPilot.Features.Security;

public sealed class SafeModeService
{
    private readonly object gate = new();
    private readonly PortableDataPaths paths;
    private readonly TerminalSessionManager terminals;
    private readonly ILogger<SafeModeService> logger;
    private readonly HashSet<Channel<bool>> subscribers = [];
    private CancellationTokenSource restrictedOperations = new();
    private ProtectionGeneration protection;
    private long protectionGeneration = 1;
    private long version;
    private bool enabled;

    public SafeModeService(
        PortableDataPaths paths,
        TerminalSessionManager terminals,
        ILogger<SafeModeService> logger)
    {
        this.paths = paths;
        this.terminals = terminals;
        this.logger = logger;
        enabled = WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).SafeModeEnabled;
        protection = new(protectionGeneration, restrictedOperations);
        if (enabled) restrictedOperations.Cancel();
    }

    public bool Enabled { get { lock (gate) return enabled; } }
    public CancellationToken RestrictedOperationsCancellation { get { lock (gate) return restrictedOperations.Token; } }

    internal Action? EnableForAuthenticationFailure()
    {
        lock (gate)
        {
            if (enabled) return null;
            var transition = CommitProtection();
            return () => CompleteAuthenticationProtection(transition);
        }
    }

    private void CompleteAuthenticationProtection(ProtectionTransition transition)
    {
        if (!CancelProtection(transition, authentication: true)) return;
        try { transition.CloseTerminals().GetAwaiter().GetResult(); }
        catch (Exception exception) { logger.LogWarning(exception, "认证失效保护已开启，关闭终端会话失败"); }
        lock (gate)
        {
            if (!IsCurrentProtection(transition)) return;
            WinPilotSettings.Update(paths.ConfigurationFile, current => current with { SafeModeEnabled = true });
        }
        logger.LogInformation("安全模式已开启");
    }

    private bool IsCurrentProtection(ProtectionTransition transition) =>
        enabled && version == transition.Version && protectionGeneration == transition.Protection.Generation
            && ReferenceEquals(restrictedOperations, transition.Protection.Source);

    public void SetEnabled(bool value)
    {
        ProtectionTransition? transition = null;
        CancellationTokenSource? retiredCancellation = null;
        lock (gate)
        {
            if (enabled == value) return;
            WinPilotSettings.Update(paths.ConfigurationFile, current => current with { SafeModeEnabled = value });
            if (value)
            {
                transition = CommitProtection();
            }
            else
            {
                enabled = false;
                version++;
                protection.Retired = true;
                retiredCancellation = protection.TakeRetiredSource();
                restrictedOperations = new CancellationTokenSource();
                protection = new(++protectionGeneration, restrictedOperations);
                PublishState(false);
            }
        }

        retiredCancellation?.Dispose();
        if (transition is not null)
        {
            CancelProtection(transition, authentication: false);
            transition.CloseTerminals().GetAwaiter().GetResult();
            lock (gate) if (!IsCurrentProtection(transition)) return;
        }
        logger.LogInformation("安全模式已{State}", value ? "开启" : "关闭");
    }

    private ProtectionTransition CommitProtection()
    {
        enabled = true;
        protection.CancellationInProgress = true;
        var transition = new ProtectionTransition(++version, protection, terminals.CaptureCloseOperation());
        PublishState(true);
        return transition;
    }

    private void PublishState(bool value)
    {
        foreach (var subscriber in subscribers) subscriber.Writer.TryWrite(value);
    }

    private bool CancelProtection(ProtectionTransition transition, bool authentication)
    {
        if (Interlocked.Exchange(ref transition.CancellationStarted, 1) != 0) return false;
        try { transition.Protection.Source.Cancel(); }
        catch (AggregateException exception)
        {
            logger.LogWarning(exception, authentication
                ? "认证失效保护已开启，取消受限操作的回调失败"
                : "安全模式已开启，取消受限操作的回调失败");
        }
        finally
        {
            CancellationTokenSource? retired;
            lock (gate)
            {
                transition.Protection.CancellationInProgress = false;
                retired = transition.Protection.TakeRetiredSource();
            }
            retired?.Dispose();
        }
        return true;
    }

    private sealed class ProtectionTransition(long version, ProtectionGeneration protection, Func<Task> closeTerminals)
    {
        internal long Version { get; } = version;
        internal ProtectionGeneration Protection { get; } = protection;
        internal Func<Task> CloseTerminals { get; } = closeTerminals;
        internal int CancellationStarted;
    }

    private sealed class ProtectionGeneration(long generation, CancellationTokenSource source)
    {
        private bool disposed;
        internal long Generation { get; } = generation;
        internal CancellationTokenSource Source { get; } = source;
        internal bool CancellationInProgress { get; set; }
        internal bool Retired { get; set; }
        internal CancellationTokenSource? TakeRetiredSource()
        {
            if (!Retired || CancellationInProgress || disposed) return null;
            disposed = true;
            return Source;
        }
    }

    public async IAsyncEnumerable<bool> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        lock (gate)
        {
            subscribers.Add(channel);
            channel.Writer.TryWrite(enabled);
        }
        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync(cancellationToken)) yield return value;
        }
        finally
        {
            lock (gate) subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
