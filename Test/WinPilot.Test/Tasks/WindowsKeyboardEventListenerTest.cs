using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using WinPilot.Core.Input;
using WinPilot.Features.Tasks;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class WindowsKeyboardEventListenerTest
{
    [Fact]
    public async Task DisposeDoesNotWaitAgainAfterStopBudgetAndDeliveryCanFinishLater()
    {
        using var logger = new ProbeLogger();
        using var listener = new WindowsKeyboardEventListener(logger);
        listener.Triggered += _ => throw new IOException("controlled delivery failure");
        logger.BlockErrors = true;
        try
        {
            Start(listener, [new(new(), [KeyboardKey.A])]);
            Input(listener, 0x41, 0x1e);
            Assert.True(logger.Entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(listener.Availability);
            using var budget = new CancellationTokenSource();
            var stopping = listener.StopAsync(budget.Token);
            budget.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.StopAsync(budget.Token));
            Assert.Single(logger.Messages.Where(item => item.Level == LogLevel.Warning));
            await Task.Run(() =>
            {
                listener.Dispose();
                listener.Dispose();
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(listener.StopAsync(CancellationToken.None).IsCompleted);
        }
        finally
        {
            logger.Release.Set();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await listener.StopAsync(cleanup.Token);
        }
        Assert.Equal(0, FindListenerWindow());
        var error = Assert.Single(logger.Messages.Where(item => item.Level == LogLevel.Error));
        Assert.IsType<IOException>(error.Exception);
        Assert.Null(listener.Availability);
    }

    [Fact]
    public async Task AvailabilityDistinguishesNormalDisableFailureAndRestart()
    {
        using var listener = new WindowsKeyboardEventListener(NullLogger<WindowsKeyboardEventListener>.Instance);
        var binding = new KeyboardTriggerBinding(new(), [KeyboardKey.A]);
        try
        {
            Assert.Null(listener.Availability);
            Start(listener, [binding]);
            Assert.True(listener.Availability);
            using (var thread = OpenListenerThread())
            {
                listener.Update([]);
                Assert.Equal(0u, WaitForSingleObject(thread, 5000));
                Assert.Equal(0, FindListenerWindow());
            }
            Assert.Null(listener.Availability);
            Start(listener, [binding]);
            Assert.True(listener.Availability);
            SendControl(FindListenerWindow(), 0x10);
            Assert.True(SpinWait.SpinUntil(() => listener.Availability == false && FindListenerWindow() == 0, TimeSpan.FromSeconds(5)));
            Start(listener, [binding]);
            Assert.True(listener.Availability);
        }
        finally
        {
            await Stop(listener);
        }
        Assert.Null(listener.Availability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedOldInputCannotTriggerReplacementAndDispatchDoesNotHoldListenerLock(bool disableFirst)
    {
        using var listener = new WindowsKeyboardEventListener(NullLogger<WindowsKeyboardEventListener>.Instance);
        var oldOwner = new object();
        var nextOwner = new object();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<object>();
        listener.Triggered += binding =>
        {
            observed.Enqueue(binding.Owner);
            if (ReferenceEquals(binding.Owner, oldOwner))
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            else
            {
                next.TrySetResult();
            }
        };
        try
        {
            Start(listener, [new(oldOwner, [KeyboardKey.A])]);
            Input(listener, 0x41, 0x1e);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Input(listener, 0x41, 0x1e, 0x80);
            listener.ReceiveInput(0x41, 0x1e, 0, 0);
            listener.ReceiveInput(0x41, 0x1e, 0x80, 0);
            await Task.Run(() =>
            {
                if (disableFirst)
                {
                    listener.Update([]);
                }
                listener.Update([new(nextOwner, [KeyboardKey.A])]);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            if (disableFirst)
            {
                Assert.Null(listener.Availability);
                release.Set();
                WaitForStart(listener);
            }
            else
            {
                SendControl(FindListenerWindow(), 0x8001);
                AdvanceClock();
                release.Set();
            }
            Input(listener, 0x41, 0x1e);
            await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { oldOwner, nextOwner }, observed.ToArray());
        }
        finally
        {
            release.Set();
            await Stop(listener);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleCleanupHonorsPendingRestartAndShutdown(bool stopDuringCleanup)
    {
        using var logger = new ProbeLogger();
        using var listener = new WindowsKeyboardEventListener(logger);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstOwner = new object();
        var latestOwner = new object();
        var observed = new ConcurrentQueue<object>();
        listener.Triggered += binding =>
        {
            observed.Enqueue(binding.Owner);
            if (ReferenceEquals(binding.Owner, firstOwner))
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            else
            {
                recovered.TrySetResult();
            }
        };
        try
        {
            Start(listener, [new(firstOwner, [KeyboardKey.A])]);
            using var oldThread = OpenListenerThread();
            Input(listener, 0x41, 0x1e);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            listener.Update([]);
            Assert.True(SpinWait.SpinUntil(() => FindListenerWindow() == 0, TimeSpan.FromSeconds(5)));
            Assert.Equal(0x102u, WaitForSingleObject(oldThread, 0));
            await Task.Run(() =>
            {
                listener.Update([new(new(), [KeyboardKey.B])]);
                listener.Update([new(latestOwner, [KeyboardKey.C])]);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(listener.Availability);
            Assert.Equal(0, FindListenerWindow());
            if (stopDuringCleanup)
            {
                using var budget = new CancellationTokenSource();
                var stopping = listener.StopAsync(budget.Token);
                budget.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
                release.Set();
                await Stop(listener);
                Assert.Equal(0, FindListenerWindow());
                Assert.Equal(new[] { firstOwner }, observed.ToArray());
            }
            else
            {
                release.Set();
                WaitForStart(listener);
                Input(listener, 0x43, 0x2e);
                await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(new[] { firstOwner, latestOwner }, observed.ToArray());
                Assert.True(listener.Availability);
            }
            Assert.Equal(0u, WaitForSingleObject(oldThread, 5000));
            Assert.DoesNotContain(logger.Messages, item => item.Level == LogLevel.Error);
            Assert.DoesNotContain(logger.Messages, item => item.Message == "发送按键监听控制消息失败");
        }
        finally
        {
            release.Set();
            await Stop(listener);
        }
    }

    [Fact]
    public async Task DisableAndReenableRacingWindowCreationPreservesLatestRules()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            using var listener = new WindowsKeyboardEventListener(NullLogger<WindowsKeyboardEventListener>.Instance);
            try
            {
                listener.Update([new(new(), [KeyboardKey.A])]);
                listener.Update([]);
                Start(listener, [new(new(), [KeyboardKey.B])]);
                Assert.True(listener.Availability);
            }
            finally
            {
                await Stop(listener);
            }
        }
    }

    [Fact]
    public async Task OverflowDropsOldBatchResetsStateAndReportsOnceWithoutBlockingProducer()
    {
        using var logger = new ProbeLogger { BlockWarnings = true };
        using var listener = new WindowsKeyboardEventListener(logger);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        listener.Triggered += _ =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            else
            {
                recovered.TrySetResult();
            }
        };
        try
        {
            Start(listener, [new(new(), [KeyboardKey.A])]);
            Input(listener, 0x41, 0x1e);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
            {
                for (var index = 0; index < 5000; index++)
                {
                    Input(listener, 0x41, 0x1e, index % 2 == 0 ? 0x80 : 0);
                }
            }).WaitAsync(TimeSpan.FromSeconds(5));
            release.Set();
            Assert.True(logger.Entered.Wait(TimeSpan.FromSeconds(5)));
            Input(listener, 0x41, 0x1e);
            logger.Release.Set();
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, count);
            Assert.Single(logger.Messages.Where(item => item.Level == LogLevel.Warning));
        }
        finally
        {
            release.Set();
            logger.Release.Set();
            await Stop(listener);
        }
    }

    [Fact]
    public async Task InjectedInputParticipatesAndExtendedFlagDistinguishesNumpadEnter()
    {
        using var listener = new WindowsKeyboardEventListener(NullLogger<WindowsKeyboardEventListener>.Instance);
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        listener.Triggered += _ =>
        {
            Interlocked.Increment(ref count);
            fired.TrySetResult();
        };
        try
        {
            Start(listener, [new(new(), [KeyboardKey.NumpadEnter])]);
            Input(listener, 0x0d, 0x1c, 0x10);
            Input(listener, 0x0d, 0x1c, 0x90);
            Input(listener, 0x0d, 0x1c, 0x11);
            Input(listener, 0x0d, 0x1c, 0x91);
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await Stop(listener);
        }
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(0x8002, 0)]
    [InlineData(0x219, 0x8000)]
    [InlineData(0x219, 0x8004)]
    public async Task ResetInvalidatesQueuedInputAndDiscardsEarlierPressedState(uint message, int wParam)
    {
        using var listener = new WindowsKeyboardEventListener(NullLogger<WindowsKeyboardEventListener>.Instance);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        listener.Triggered += _ =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            else
            {
                recovered.TrySetResult();
            }
        };
        try
        {
            Start(listener, [new(new(), [KeyboardKey.A])]);
            Input(listener, 0x41, 0x1e);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Input(listener, 0x41, 0x1e, 0x80);
            Input(listener, 0x41, 0x1e);
            SendControl(FindListenerWindow(), message, wParam);
            AdvanceClock();
            release.Set();
            Input(listener, 0x41, 0x1e);
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, count);
        }
        finally
        {
            release.Set();
            await Stop(listener);
        }
    }

    [Fact]
    public async Task StopRacingWithWindowCreationCompletesAndRepeatedDisposalIsSafe()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            using var listener = new WindowsKeyboardEventListener(NullLogger<WindowsKeyboardEventListener>.Instance);
            listener.Update([new(new(), [KeyboardKey.A])]);
            await Stop(listener);
            listener.Dispose();
            listener.Dispose();
            listener.Update([new(new(), [KeyboardKey.B])]);
            await Stop(listener);
        }
    }

    [Fact]
    public async Task ConcurrentRuleUpdatesAndStopDoNotLeaveAWindowThreadRunning()
    {
        using var listener = new WindowsKeyboardEventListener(NullLogger<WindowsKeyboardEventListener>.Instance);
        using var start = new Barrier(3);
        var update = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 100; i++)
            {
                listener.Update(i % 2 == 0 ? [new(new(), [KeyboardKey.A])] : []);
            }
        });
        var stop = Task.Run(async () =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            await Stop(listener);
        });
        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(update, stop).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void Start(WindowsKeyboardEventListener listener, KeyboardTriggerBinding[] bindings)
    {
        listener.Update(bindings);
        WaitForStart(listener);
    }

    private static void WaitForStart(WindowsKeyboardEventListener listener)
    {
        nint window = 0;
        Assert.True(SpinWait.SpinUntil(
            () => listener.Availability == true && (window = FindListenerWindow()) != 0,
            TimeSpan.FromSeconds(5)));
        SendControl(window, 0x8001);
        AdvanceClock();
    }

    private static void AdvanceClock()
    {
        var before = Environment.TickCount64;
        Assert.True(SpinWait.SpinUntil(() => Environment.TickCount64 > before, TimeSpan.FromSeconds(5)));
    }

    private static void Input(
        WindowsKeyboardEventListener listener,
        int key,
        int scan,
        int flags = 0) =>
        listener.ReceiveInput(key, scan, flags, unchecked((uint)Environment.TickCount64));

    private static async Task Stop(WindowsKeyboardEventListener listener)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await listener.StopAsync(budget.Token);
    }

    private static void SendControl(nint window, uint message, int wParam = 0)
    {
        nint data = 0;
        try
        {
            if (message == 0x219)
            {
                data = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceInterface>());
                Marshal.StructureToPtr(new DeviceInterface
                {
                    Size = Marshal.SizeOf<DeviceInterface>(),
                    Type = 5,
                    ClassGuid = new("884b96c3-56ef-11d1-bc8c-00a0c91405dd")
                }, data, false);
            }
            Assert.NotEqual(0, SendMessageTimeout(window, message, wParam, data, 2, 5000, out _));
        }
        finally
        {
            if (data != 0)
            {
                Marshal.FreeHGlobal(data);
            }
        }
    }

    private static nint FindListenerWindow()
    {
        nint window = 0;
        while ((window = FindWindowEx(-3, window, null, "WinPilot keyboard listener")) != 0)
        {
            GetWindowThreadProcessId(window, out var process);
            if (process == Environment.ProcessId)
            {
                return window;
            }
        }
        return 0;
    }

    private static SafeWaitHandle OpenListenerThread()
    {
        var threadId = GetWindowThreadProcessId(FindListenerWindow(), out _);
        var thread = OpenThread(0x100000, false, threadId);
        Assert.False(thread.IsInvalid);
        return thread;
    }

    private sealed class ProbeLogger : ILogger<WindowsKeyboardEventListener>, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        internal readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Messages = new();
        internal bool BlockErrors, BlockWarnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level,
            EventId id,
            TState state,
            Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue((level, formatter(state, error), error));
            if (level == LogLevel.Error && BlockErrors || level == LogLevel.Warning && BlockWarnings)
            {
                Entered.Set();
                Assert.True(Release.Wait(TimeSpan.FromSeconds(15)));
            }
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(
        nint parent,
        nint after,
        string? className,
        string title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint timeout);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nuint result);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterface
    {
        internal int Size, Type, Reserved;
        internal Guid ClassGuid;
        internal ushort Name;
    }
}
