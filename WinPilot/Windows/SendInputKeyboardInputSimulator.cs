using System.ComponentModel;
using System.Runtime.InteropServices;
using WinPilot.Core.Input;

namespace WinPilot.Windows;

public sealed class SendInputKeyboardInputSimulator : IKeyboardInputSimulator
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtendedKey = 0x0001;

    public void Send(IReadOnlyList<KeyboardKey> keys)
    {
        var inputs = new Input[keys.Count * 2];
        for (var index = 0; index < keys.Count; index++)
        {
            inputs[index] = CreateInput(keys[index], false);
            inputs[inputs.Length - index - 1] = CreateInput(keys[index], true);
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        ValidateSentInputCount(sent, inputs.Length, Marshal.GetLastWin32Error());
    }

    internal static void ValidateSentInputCount(uint sent, int expected, int errorCode)
    {
        if (sent != expected) throw new Win32Exception(errorCode, "无法发送键盘输入");
    }

    private static Input CreateInput(KeyboardKey key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = GetVirtualKey(key),
                Flags = (keyUp ? KeyEventKeyUp : 0) | (IsExtendedKey(key) ? KeyEventExtendedKey : 0)
            }
        }
    };

    private static ushort GetVirtualKey(KeyboardKey key) => key switch
    {
        KeyboardKey.Ctrl => 0x11,
        KeyboardKey.Shift => 0x10,
        KeyboardKey.Alt => 0x12,
        KeyboardKey.Win => 0x5B,
        KeyboardKey.CapsLock => 0x14,
        KeyboardKey.NumLock => 0x90,
        KeyboardKey.ScrollLock => 0x91,
        KeyboardKey.Escape => 0x1B,
        KeyboardKey.PrintScreen => 0x2C,
        KeyboardKey.Pause => 0x13,
        KeyboardKey.Tab => 0x09,
        KeyboardKey.Enter => 0x0D,
        KeyboardKey.Space => 0x20,
        KeyboardKey.Backspace => 0x08,
        KeyboardKey.Insert => 0x2D,
        KeyboardKey.Delete => 0x2E,
        KeyboardKey.Up => 0x26,
        KeyboardKey.Down => 0x28,
        KeyboardKey.Left => 0x25,
        KeyboardKey.Right => 0x27,
        KeyboardKey.Home => 0x24,
        KeyboardKey.End => 0x23,
        KeyboardKey.PageUp => 0x21,
        KeyboardKey.PageDown => 0x22,
        KeyboardKey.Apps => 0x5D,
        KeyboardKey.OemTilde => 0xC0,
        KeyboardKey.OemMinus => 0xBD,
        KeyboardKey.OemPlus => 0xBB,
        KeyboardKey.OemOpenBrackets => 0xDB,
        KeyboardKey.OemCloseBrackets => 0xDD,
        KeyboardKey.OemPipe => 0xDC,
        KeyboardKey.OemSemicolon => 0xBA,
        KeyboardKey.OemQuotes => 0xDE,
        KeyboardKey.OemComma => 0xBC,
        KeyboardKey.OemPeriod => 0xBE,
        KeyboardKey.OemQuestion => 0xBF,
        >= KeyboardKey.F1 and <= KeyboardKey.F12 => (ushort)(0x70 + key - KeyboardKey.F1),
        >= KeyboardKey.D0 and <= KeyboardKey.D9 => (ushort)(0x30 + key - KeyboardKey.D0),
        >= KeyboardKey.Numpad0 and <= KeyboardKey.Numpad9 => (ushort)(0x60 + key - KeyboardKey.Numpad0),
        KeyboardKey.NumpadMultiply => 0x6A,
        KeyboardKey.NumpadAdd => 0x6B,
        KeyboardKey.NumpadSubtract => 0x6D,
        KeyboardKey.NumpadDecimal => 0x6E,
        KeyboardKey.NumpadDivide => 0x6F,
        KeyboardKey.NumpadEnter => 0x0D,
        >= KeyboardKey.A and <= KeyboardKey.Z => (ushort)(0x41 + key - KeyboardKey.A),
        _ => throw new KeyboardInputException("包含不支持的按键")
    };

    private static bool IsExtendedKey(KeyboardKey key) => key is KeyboardKey.Win or KeyboardKey.NumLock or KeyboardKey.PrintScreen or KeyboardKey.Insert
        or KeyboardKey.Delete or KeyboardKey.Up or KeyboardKey.Down or KeyboardKey.Left or KeyboardKey.Right or KeyboardKey.Home or KeyboardKey.End
        or KeyboardKey.PageUp or KeyboardKey.PageDown or KeyboardKey.Apps or KeyboardKey.NumpadDivide or KeyboardKey.NumpadEnter;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
