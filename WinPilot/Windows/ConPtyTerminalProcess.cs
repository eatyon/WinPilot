using WinPilot.Features.Terminal;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WinPilot.Windows;

internal sealed class ConPtyTerminalProcess : ITerminalProcess
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint HandleFlagInherit = 0x00000001;
    public FileStream Input { get; }
    public FileStream Output { get; }
    Stream ITerminalProcess.Input => Input;
    Stream ITerminalProcess.Output => Output;
    private readonly SafeFileHandle processHandle;
    private IntPtr pseudoConsole;

    private ConPtyTerminalProcess(IntPtr inputWrite, IntPtr outputRead, IntPtr process, IntPtr pseudoConsole)
    {
        Input = new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write);
        Output = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read);
        processHandle = new SafeFileHandle(process, true);
        this.pseudoConsole = pseudoConsole;
    }

    public static ConPtyTerminalProcess Start(string shell, string workingDirectory)
    {
        var executable = shell == "cmd" ? Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe") : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到终端程序");
        var arguments = shell == "cmd" ? string.Empty : "-NoLogo -NoProfile -NoExit -Command \"[Console]::InputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8\"";
        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero, outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero, console = IntPtr.Zero, attributes = IntPtr.Zero, process = IntPtr.Zero, thread = IntPtr.Zero;
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            Ensure(CreatePipe(out inputRead, out inputWrite, ref security, 0), "无法创建终端输入管道");
            Ensure(CreatePipe(out outputRead, out outputWrite, ref security, 0), "无法创建终端输出管道");
            Ensure(SetHandleInformation(inputWrite, HandleFlagInherit, 0), "无法设置终端输入管道");
            Ensure(SetHandleInformation(outputRead, HandleFlagInherit, 0), "无法设置终端输出管道");
            Ensure(CreatePseudoConsole(new Coord(120, 40), inputRead, outputWrite, 0, out console), "无法创建 ConPTY");
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((nint)size));
            Ensure(InitializeProcThreadAttributeList(attributes, 1, 0, ref size), "无法初始化 ConPTY 属性");
            Ensure(UpdateProcThreadAttribute(attributes, 0, (IntPtr)ProcThreadAttributePseudoConsole, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "无法附加 ConPTY 属性");
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() }, AttributeList = attributes };
            var commandLine = new StringBuilder($"\"{executable}\" {arguments}");
            Ensure(CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment | ExtendedStartupInfoPresent, IntPtr.Zero, workingDirectory, ref startup, out var info), "无法启动 ConPTY 终端进程");
            process = info.Process;
            thread = info.Thread;
            CloseHandle(inputRead); inputRead = IntPtr.Zero;
            CloseHandle(outputWrite); outputWrite = IntPtr.Zero;
            CloseHandle(thread); thread = IntPtr.Zero;
            var result = new ConPtyTerminalProcess(inputWrite, outputRead, process, console);
            inputWrite = IntPtr.Zero; outputRead = IntPtr.Zero; process = IntPtr.Zero; console = IntPtr.Zero;
            return result;
        }
        finally
        {
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            CloseHandleIfNeeded(inputRead); CloseHandleIfNeeded(inputWrite); CloseHandleIfNeeded(outputRead); CloseHandleIfNeeded(outputWrite); CloseHandleIfNeeded(process); CloseHandleIfNeeded(thread);
            if (console != IntPtr.Zero) ClosePseudoConsole(console);
        }
    }

    public int? ExitCode
    {
        get
        {
            Ensure(GetExitCodeProcess(processHandle, out var code), "读取终端退出码失败");
            return code == 259 ? null : unchecked((int)code);
        }
    }

    public bool HasExited() => WaitForSingleObject(processHandle, 0) == 0;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var result = WaitForSingleObject(processHandle, uint.MaxValue);
        if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "等待终端进程退出失败");
    }, cancellationToken);
    public void Resize(short columns, short rows)
    {
        var console = Interlocked.CompareExchange(ref pseudoConsole, IntPtr.Zero, IntPtr.Zero);
        if (console == IntPtr.Zero) throw new ObjectDisposedException(nameof(ConPtyTerminalProcess));
        Ensure(ResizePseudoConsole(console, new Coord(columns, rows)), "无法调整 ConPTY 大小");
    }
    public void Kill() { if (!processHandle.IsInvalid) TerminateProcess(processHandle, 1); }
    public void ClosePseudoConsoleHandle()
    {
        var console = Interlocked.Exchange(ref pseudoConsole, IntPtr.Zero);
        if (console != IntPtr.Zero) ClosePseudoConsole(console);
    }
    public ValueTask CloseOutputAsync() => Output.DisposeAsync();
    public async ValueTask DisposeAsync()
    {
        await Input.DisposeAsync();
        await Output.DisposeAsync();
        processHandle.Dispose();
        ClosePseudoConsoleHandle();
    }

    private static void Ensure(bool result, string message) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error(), message); }
    private static void Ensure(int result, string message) { if (result < 0) throw new Win32Exception(result, message); }
    private static void CloseHandleIfNeeded(IntPtr handle) { if (handle != IntPtr.Zero) CloseHandle(handle); }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x; public readonly short Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int cb; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2; public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, uint count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInfo);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeHandle process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(SafeHandle process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);
}
