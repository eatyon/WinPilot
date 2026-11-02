using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32;
using WinPilot.Core.Input;
using WinPilot.Features.Tasks;

namespace WinPilot.Windows;

/// <summary>
/// 在有任务按键绑定时按需运行监听线程和消息窗口，将输入排队后交给组合键匹配器。
/// </summary>
public sealed class WindowsKeyboardEventListener(ILogger<WindowsKeyboardEventListener> logger) : IDisposable
{
    private readonly ILogger<WindowsKeyboardEventListener> logger = logger;
    private const int CloseMessage = 0x10;
    private const int UpdateMessage = 0x8001;
    private const int ResetMessage = 0x8002;
    private const int FailureMessage = 0x8003;
    private const int InputCapacity = 4096;
    private readonly object gate = new();
    private readonly KeyboardTriggerMatcher matcher = new();
    private readonly Dictionary<object, KeyboardTriggerBinding> bindings = new(ReferenceEqualityComparer.Instance);
    private ListenerRun? activeRun;
    private nint handle;
    private bool disposed;
    private bool? available;
    private int stopWaitWarningLogged;

    internal event Action<KeyboardTriggerBinding>? Triggered;

    /// <summary>
    /// 当前绑定的监听可用性；无绑定、已释放或尚未确定监听结果时为 null。
    /// </summary>
    internal bool? Availability
    {
        get
        {
            lock (gate)
            {
                return disposed || bindings.Count == 0 ? null : available;
            }
        }
    }

    internal void Update(IReadOnlyList<KeyboardTriggerBinding> next)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            var wasEnabled = bindings.Count > 0;
            var current = next.Select(binding => bindings.TryGetValue(binding.Owner, out var existing) ? existing : binding).ToArray();
            bindings.Clear();
            foreach (var binding in current)
            {
                bindings[binding.Owner] = binding;
            }
            matcher.Update(current, Environment.TickCount64);
            if (wasEnabled != (current.Length > 0))
            {
                if (available != false)
                {
                    available = null;
                }
                if (activeRun is { } changing)
                {
                    Interlocked.Increment(ref changing.Epoch);
                }
                matcher.Reset(Environment.TickCount64);
            }
            // 没有绑定即请求退出消息循环，不为已禁用的触发条件保留窗口和线程。
            if (current.Length == 0)
            {
                available = null;
                if (activeRun is { IdleStopRequested: false } retiring)
                {
                    retiring.IdleStopRequested = true;
                    retiring.Retired = true;
                    if (handle != 0)
                    {
                        PostControlMessage(handle, CloseMessage);
                    }
                }
            }
            else if (activeRun is null)
            {
                StartRun();
            }
            else if (!activeRun.Retired && handle != 0)
            {
                PostControlMessage(handle, UpdateMessage);
            }
        }
    }

    private void StartRun()
    {
        var run = new ListenerRun();
        available = null;
        activeRun = run;
        try
        {
            var thread = new Thread(() => Run(run)) { IsBackground = true, Name = "WinPilot keyboard listener" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception exception)
        {
            activeRun = null;
            available = false;
            run.Input.Writer.TryComplete();
            run.Completed.TrySetResult();
            logger.LogError(exception, "无法启动按键监听");
        }
    }

    private void Run(ListenerRun run)
    {
        var delivery = Task.CompletedTask;
        try
        {
            using var window = new InputWindow(this, run);
            lock (gate)
            {
                if (disposed || run.Retired)
                {
                    return;
                }
                handle = window.Handle;
            }
            delivery = DeliverAsync(run);
            var sessionSubscribed = false;
            var powerSubscribed = false;
            try
            {
                SystemEvents.SessionSwitch += SessionChanged;
                sessionSubscribed = true;
                SystemEvents.PowerModeChanged += PowerChanged;
                powerSubscribed = true;
                window.Apply();
                Application.Run();
            }
            finally
            {
                try
                {
                    if (sessionSubscribed)
                    {
                        SystemEvents.SessionSwitch -= SessionChanged;
                    }
                }
                finally
                {
                    if (powerSubscribed)
                    {
                        SystemEvents.PowerModeChanged -= PowerChanged;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            MarkUnavailable(run);
            logger.LogError(exception, "按键监听不可用");
        }
        finally
        {
            lock (gate)
            {
                run.Retired = true;
                if (ReferenceEquals(activeRun, run))
                {
                    handle = 0;
                }
            }
            run.Input.Writer.TryComplete();
            try
            {
                delivery.GetAwaiter().GetResult();
            }
            finally
            {
                lock (gate)
                {
                    var restart = false;
                    if (ReferenceEquals(activeRun, run))
                    {
                        // 空闲退出期间可能新增绑定，等旧线程收尾完成后再创建新一轮监听。
                        restart = !disposed && bindings.Count > 0 && run.IdleStopRequested;
                        if (!disposed && bindings.Count > 0)
                        {
                            available = restart ? null : false;
                        }
                        activeRun = null;
                        matcher.Reset();
                    }
                    run.Completed.TrySetResult();
                    if (restart)
                    {
                        StartRun();
                    }
                }
            }
        }
    }

    internal void ReceiveInput(
        int virtualKey,
        int scanCode,
        int flags,
        uint time)
    {
        var run = Volatile.Read(ref activeRun);
        if (run is null || Volatile.Read(ref run.Retired) || virtualKey is <= 0 or >= 255)
        {
            return;
        }
        var input = new HookInput(virtualKey, scanCode, flags, time, Volatile.Read(ref run.Epoch));
        if (run.Input.Writer.TryWrite(input))
        {
            return;
        }
        // 输入丢失后换代，使消费端重置按键状态，避免把不完整序列匹配为快捷键。
        Interlocked.Increment(ref run.Epoch);
        Interlocked.Exchange(ref run.Overflowed, 1);
    }

    private async Task DeliverAsync(ListenerRun run)
    {
        var processedEpoch = -1;
        var overflowLogged = false;
        try
        {
            await foreach (var input in run.Input.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!overflowLogged && Volatile.Read(ref run.Overflowed) != 0)
                {
                    overflowLogged = true;
                    logger.LogWarning("按键监听输入队列已满，已丢弃旧批次并重置匹配状态");
                }
                IReadOnlyList<KeyboardTriggerBinding> matches;
                lock (gate)
                {
                    if (disposed
                        || run.Retired
                        || !ReferenceEquals(activeRun, run)
                        || bindings.Count == 0
                        || input.Epoch != Volatile.Read(ref run.Epoch))
                    {
                        continue;
                    }
                    if (processedEpoch != input.Epoch)
                    {
                        matcher.Reset();
                        processedEpoch = input.Epoch;
                    }
                    var extended = (input.Flags & 1) != 0;
                    var identity = KeyboardInputIdentity.FromScanCode(input.ScanCode, input.VirtualKey, extended);
                    var mapped = MapKey(input.VirtualKey, input.ScanCode, extended);
                    var now = Environment.TickCount64;
                    var eventTime = now - unchecked((uint)now - input.Time);
                    matches = matcher.Input(identity, mapped, (input.Flags & 0x80) == 0, eventTime);
                }
                foreach (var match in matches)
                {
                    Triggered?.Invoke(match);
                }
            }
        }
        catch (Exception exception)
        {
            MarkUnavailable(run);
            logger.LogError(exception, "处理按键监听失败，监听已停止");
            lock (gate)
            {
                if (ReferenceEquals(activeRun, run) && handle != 0)
                {
                    PostControlMessage(handle, CloseMessage);
                }
            }
        }
    }

    private void MarkUnavailable(ListenerRun run)
    {
        lock (gate)
        {
            if (ReferenceEquals(activeRun, run) && !run.IdleStopRequested)
            {
                available = false;
            }
        }
    }

    private void MarkAvailable(ListenerRun run)
    {
        lock (gate)
        {
            if (!disposed
                && !run.Retired
                && bindings.Count > 0
                && ReferenceEquals(activeRun, run)
                && available != false)
            {
                available = true;
            }
        }
    }

    private void SessionChanged(object sender, SessionSwitchEventArgs args) => RequestReset();

    private void PowerChanged(object sender, PowerModeChangedEventArgs args) => RequestReset();

    private void RequestReset()
    {
        lock (gate)
        {
            if (handle != 0)
            {
                PostControlMessage(handle, ResetMessage);
            }
        }
    }

    internal async Task StopAsync(CancellationToken token)
    {
        var stopping = RequestStop();
        try
        {
            await stopping.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!stopping.IsCompleted && Interlocked.Exchange(ref stopWaitWarningLogged, 1) == 0)
            {
                logger.LogWarning("停止等待已取消，按键监听线程尚未退出");
            }
            throw;
        }
    }

    private Task RequestStop()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                bindings.Clear();
                matcher.Update([]);
                if (handle != 0 && activeRun is { Retired: false })
                {
                    PostControlMessage(handle, CloseMessage);
                }
            }
            return activeRun?.Completed.Task ?? Task.CompletedTask;
        }
    }

    public void Dispose() => _ = RequestStop();

    private void PostControlMessage(nint window, int message)
    {
        if (!PostMessage(window, message, 0, 0))
        {
            logger.LogWarning(new Win32Exception(Marshal.GetLastWin32Error()), "发送按键监听控制消息失败");
        }
    }

    internal static KeyboardKey? MapKey(int virtualKey, int scanCode, bool extended)
    {
        if (!extended && virtualKey is >= 0x21 and <= 0x2e)
        {
            var numpad = scanCode switch
            {
                0x47 => KeyboardKey.Numpad7,
                0x48 => KeyboardKey.Numpad8,
                0x49 => KeyboardKey.Numpad9,
                0x4b => KeyboardKey.Numpad4,
                0x4c => KeyboardKey.Numpad5,
                0x4d => KeyboardKey.Numpad6,
                0x4f => KeyboardKey.Numpad1,
                0x50 => KeyboardKey.Numpad2,
                0x51 => KeyboardKey.Numpad3,
                0x52 => KeyboardKey.Numpad0,
                0x53 => KeyboardKey.NumpadDecimal,
                _ => (KeyboardKey?)null
            };
            if (numpad is not null)
            {
                return numpad;
            }
        }
        return virtualKey switch
        {
            0x10 or 0xa0 or 0xa1 => KeyboardKey.Shift,
            0x11 or 0xa2 or 0xa3 => KeyboardKey.Ctrl,
            0x12 or 0xa4 or 0xa5 => KeyboardKey.Alt,
            0x5b or 0x5c => KeyboardKey.Win,
            0x0d => extended ? KeyboardKey.NumpadEnter : KeyboardKey.Enter,
            0x0c when scanCode == 0x4c => KeyboardKey.Numpad5,
            _ => KeyMap.GetValueOrDefault(virtualKey)
        };
    }

    private static readonly IReadOnlyDictionary<int, KeyboardKey?> KeyMap = Enum
        .GetValues<KeyboardKey>()
        .Where(key => key != KeyboardKey.NumpadEnter)
        .ToDictionary(key => (int)SendInputKeyboardInputSimulator.GetVirtualKey(key), key => (KeyboardKey?)key);

    private readonly record struct HookInput(
        int VirtualKey,
        int ScanCode,
        int Flags,
        uint Time,
        int Epoch);

    private sealed class ListenerRun
    {
        internal readonly Channel<HookInput> Input = Channel.CreateBounded<HookInput>(new BoundedChannelOptions(InputCapacity)
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Epoch;
        internal int Overflowed;
        internal bool Retired;
        internal bool IdleStopRequested;
    }

    private sealed class InputWindow : NativeWindow, IDisposable
    {
        private readonly WindowsKeyboardEventListener owner;
        private readonly ListenerRun run;
        private readonly KeyboardHook keyboardHook;
        private readonly DesktopChanged desktopChanged;
        private nint hook;
        private nint desktopHook;
        private nint deviceNotification;
        private Exception? callbackFailure;

        internal InputWindow(WindowsKeyboardEventListener owner, ListenerRun run)
        {
            this.owner = owner;
            this.run = run;
            keyboardHook = OnKeyboard;
            desktopChanged = (
                _,
                _,
                _,
                _,
                _,
                _,
                _) => owner.RequestReset();
            CreateHandle(new CreateParams { Caption = "WinPilot keyboard listener", Parent = new nint(-3) });
        }

        internal void Apply()
        {
            bool enabled;
            lock (owner.gate)
            {
                enabled = !owner.disposed && !run.Retired && owner.bindings.Count > 0;
            }
            if (enabled == (hook != 0))
            {
                if (enabled)
                {
                    owner.MarkAvailable(run);
                }
                return;
            }
            if (enabled)
            {
                hook = SetWindowsHookEx(13, keyboardHook, GetModuleHandle(null), 0);
                if (hook == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                desktopHook = SetWinEventHook(0x20, 0x20, 0, desktopChanged, 0, 0, 0);
                if (desktopHook == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var filter = new DeviceInterface
                {
                    Size = Marshal.SizeOf<DeviceInterface>(),
                    Type = 5,
                    ClassGuid = new("884b96c3-56ef-11d1-bc8c-00a0c91405dd")
                };
                deviceNotification = RegisterDeviceNotification(Handle, ref filter, 0);
                if (deviceNotification == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            else
            {
                ReleaseHooks();
            }
            Reset();
            if (enabled)
            {
                owner.MarkAvailable(run);
            }
        }

        private void Reset()
        {
            lock (owner.gate)
            {
                Interlocked.Increment(ref run.Epoch);
                owner.matcher.Reset(Environment.TickCount64);
            }
        }

        private nint OnKeyboard(int code, nint wParam, nint lParam)
        {
            if (code == 0 && (uint)wParam is 0x100 or 0x101 or 0x104 or 0x105)
            {
                try
                {
                    var input = Marshal.PtrToStructure<KeyboardData>(lParam);
                    owner.ReceiveInput((int)input.VirtualKey, (int)input.ScanCode, (int)input.Flags, input.Time);
                }
                catch (Exception exception)
                {
                    if (Interlocked.CompareExchange(ref callbackFailure, exception, null) is null)
                    {
                        PostMessage(Handle, FailureMessage, 0, 0);
                    }
                }
            }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        protected override void WndProc(ref Message message)
        {
            try
            {
                if (message.Msg == CloseMessage)
                {
                    Application.ExitThread();
                    return;
                }
                if (message.Msg == UpdateMessage)
                {
                    Apply();
                }
                else if (message.Msg == ResetMessage)
                {
                    Reset();
                }
                else if (message.Msg == 0x219 && (int)message.WParam is 0x8000 or 0x8004)
                {
                    Reset();
                }
                else if (message.Msg == FailureMessage && callbackFailure is { } exception)
                {
                    throw exception;
                }
            }
            catch (Exception exception)
            {
                owner.MarkUnavailable(run);
                owner.logger.LogError(exception, "处理按键监听失败，监听已停止");
                Application.ExitThread();
            }
            base.WndProc(ref message);
        }

        private void ReleaseHooks()
        {
            if (hook != 0)
            {
                if (!UnhookWindowsHookEx(hook))
                {
                    owner.logger.LogWarning(new Win32Exception(Marshal.GetLastWin32Error()), "注销按键监听失败");
                }
                hook = 0;
            }
            if (desktopHook != 0)
            {
                if (!UnhookWinEvent(desktopHook))
                {
                    owner.logger.LogWarning(new Win32Exception(Marshal.GetLastWin32Error()), "解除桌面切换监听失败");
                }
                desktopHook = 0;
            }
            if (deviceNotification != 0)
            {
                if (!UnregisterDeviceNotification(deviceNotification))
                {
                    owner.logger.LogWarning(new Win32Exception(Marshal.GetLastWin32Error()), "注销键盘设备通知失败");
                }
                deviceNotification = 0;
            }
        }

        public void Dispose()
        {
            try
            {
                ReleaseHooks();
            }
            finally
            {
                DestroyHandle();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        internal uint VirtualKey, ScanCode, Flags, Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterface
    {
        internal int Size, Type, Reserved;
        internal Guid ClassGuid;
        internal ushort Name;
    }

    private delegate nint KeyboardHook(int code, nint wParam, nint lParam);

    private delegate void DesktopChanged(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint thread,
        uint time);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int type,
        KeyboardHook callback,
        nint module,
        uint thread);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint min,
        uint max,
        nint module,
        DesktopChanged callback,
        uint process,
        uint thread,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)]
    private static extern nint RegisterDeviceNotification(nint window, ref DeviceInterface filter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(nint notification);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessage(
        nint handle,
        int message,
        nint wParam,
        nint lParam);
}
