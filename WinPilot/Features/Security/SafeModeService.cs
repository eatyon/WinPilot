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
        if (enabled) restrictedOperations.Cancel();
    }

    public bool Enabled { get { lock (gate) return enabled; } }
    public CancellationToken RestrictedOperationsCancellation { get { lock (gate) return restrictedOperations.Token; } }

    public void SetEnabled(bool value)
    {
        Channel<bool>[] currentSubscribers;
        CancellationTokenSource? retiredCancellation = null;
        lock (gate)
        {
            if (enabled == value) return;
            WinPilotSettings.Update(paths.ConfigurationFile, current => current with { SafeModeEnabled = value });
            enabled = value;
            if (value) restrictedOperations.Cancel();
            else
            {
                retiredCancellation = restrictedOperations;
                restrictedOperations = new CancellationTokenSource();
            }
            currentSubscribers = subscribers.ToArray();
        }

        retiredCancellation?.Dispose();
        foreach (var subscriber in currentSubscribers) subscriber.Writer.TryWrite(value);
        if (value) terminals.CloseAllAsync().GetAwaiter().GetResult();
        logger.LogInformation("安全模式已{State}", value ? "开启" : "关闭");
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
