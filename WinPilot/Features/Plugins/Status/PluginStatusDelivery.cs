using System.Runtime.ExceptionServices;

namespace WinPilot.Features.Plugins;

internal sealed class PluginStatusDelivery
{
    private readonly object gate = new();
    private readonly Queue<Delivery> pending = new();
    private readonly AsyncLocal<CallbackContext?> callbackContext = new();
    private int ownerThread;
    private long version;
    private Delivery? activeRoot;

    public Delivery Enqueue(Action<long> callback)
    {
        lock (gate)
        {
            var root = ownerThread == Environment.CurrentManagedThreadId ? activeRoot
                : callbackContext.Value?.Root is { Completed: false } inherited ? inherited : null;
            var delivery = new Delivery(++version, callback, root);
            delivery.Root.Remaining++;
            pending.Enqueue(delivery);
            return delivery;
        }
    }

    public void Deliver(Delivery requested)
    {
        lock (gate)
        {
            if (requested.Root.Completed)
            {
                requested.Root.ThrowIfFailed();
                return;
            }
            if (ownerThread == Environment.CurrentManagedThreadId || ReferenceEquals(requested.Root, callbackContext.Value?.Root))
            {
                return;
            }
            if (ownerThread == 0) ownerThread = Environment.CurrentManagedThreadId;
            else
            {
                while (!requested.Root.Completed) Monitor.Wait(gate);
                requested.Root.ThrowIfFailed();
                return;
            }
        }
        while (true)
        {
            Delivery current;
            lock (gate)
            {
                if (!pending.TryDequeue(out current!))
                {
                    ownerThread = 0;
                    activeRoot = null;
                    break;
                }
                activeRoot = current.Root;
            }
            var previousContext = callbackContext.Value;
            var context = new CallbackContext(current.Root);
            callbackContext.Value = context;
            try { current.Callback(current.Version); }
            catch (Exception exception) { (current.Root.Errors ??= []).Add(exception); }
            finally { callbackContext.Value = previousContext; }
            lock (gate)
            {
                context.Root = null;
                if (--current.Root.Remaining == 0)
                {
                    current.Root.Completed = true;
                    Monitor.PulseAll(gate);
                }
            }
        }
        requested.Root.ThrowIfFailed();
    }

    private sealed class CallbackContext(Delivery root)
    {
        public Delivery? Root { get; set; } = root;
    }

    internal sealed class Delivery(long version, Action<long> callback, Delivery? root)
    {
        public Delivery Root => root ?? this;
        public long Version { get; } = version;
        public Action<long> Callback { get; } = callback;
        public List<Exception>? Errors { get; set; }
        public int Remaining { get; set; }
        public bool Completed { get; set; }
        public void ThrowIfFailed()
        {
            if (Errors is not { Count: > 0 }) return;
            if (Errors.Count == 1) ExceptionDispatchInfo.Capture(Errors[0]).Throw();
            throw new AggregateException(Errors);
        }
    }
}
