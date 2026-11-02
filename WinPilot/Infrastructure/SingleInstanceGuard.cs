using System.Security.Principal;

namespace WinPilot.Infrastructure;

/// <summary>
/// 按 Windows 用户限制程序单实例，并在旧实例正在退出时为新实例提供有限等待。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(30);
    private readonly EventWaitHandle shutdownEvent;
    private readonly ManualResetEventSlim releaseRequested;
    private readonly Thread ownershipThread;
    private bool disposed;

    private SingleInstanceGuard(
        EventWaitHandle shutdownEvent,
        ManualResetEventSlim releaseRequested,
        Thread ownershipThread)
    {
        this.shutdownEvent = shutdownEvent;
        this.releaseRequested = releaseRequested;
        this.ownershipThread = ownershipThread;
    }

    public static SingleInstanceGuard? TryAcquire()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("无法获取当前 Windows 用户 SID");
        var mutexName = $@"Global\WinPilot.{userSid.Value}";
        return TryAcquire(mutexName, mutexName + ".ShuttingDown");
    }

    internal static SingleInstanceGuard? TryAcquire(string mutexName, string shutdownEventName)
    {
        var shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, shutdownEventName);
        // 只有旧实例已声明正在退出时才等待；正常运行中的实例直接使本次获取失败。
        var waitTimeout = shutdownEvent.WaitOne(0) ? ShutdownWaitTimeout : TimeSpan.Zero;
        var acquisitionCompleted = new ManualResetEventSlim(false);
        var releaseRequested = new ManualResetEventSlim(false);
        var acquired = false;

        // Mutex 必须由获得所有权的线程释放，独立线程持有它以适应调用方跨线程释放。
        var ownershipThread = new Thread(() =>
        {
            using var mutex = new Mutex(false, mutexName);
            try
            {
                try
                {
                    acquired = mutex.WaitOne(waitTimeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
            }
            finally
            {
                acquisitionCompleted.Set();
            }

            if (!acquired)
            {
                return;
            }
            releaseRequested.Wait();
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true,
            Name = "WinPilot single-instance guard"
        };

        ownershipThread.Start();
        acquisitionCompleted.Wait();
        acquisitionCompleted.Dispose();

        if (!acquired)
        {
            ownershipThread.Join();
            releaseRequested.Dispose();
            shutdownEvent.Dispose();
            return null;
        }

        shutdownEvent.Reset();
        return new SingleInstanceGuard(shutdownEvent, releaseRequested, ownershipThread);
    }

    public void BeginShutdown() => shutdownEvent.Set();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        releaseRequested.Set();
        ownershipThread.Join();
        releaseRequested.Dispose();
        shutdownEvent.Dispose();
    }
}
