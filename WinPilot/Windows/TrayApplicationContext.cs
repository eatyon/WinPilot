using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinPilot.Core;
using WinPilot.Features.Security;

namespace WinPilot.Windows;

public sealed class TrayApplicationContext : ApplicationContext
{
    private bool resourcesDisposed;
    private readonly ILogger<TrayApplicationContext> logger;
    private readonly NotifyIcon notifyIcon;
    private readonly Icon trayIcon;
    private readonly NativeTrayMenu trayMenu;
    private readonly Uri webAddress;

    public TrayApplicationContext(Uri webAddress, ILogger<TrayApplicationContext> logger, SafeModeService safeMode, PortableDataPaths paths, Action beginShutdown)
    {
        this.webAddress = webAddress;
        this.logger = logger;
        trayMenu = new NativeTrayMenu(
            OpenWebPage,
            () => safeMode.Enabled,
            () => safeMode.SetEnabled(!safeMode.Enabled),
            () => OpenDirectory(paths.ApplicationDirectory),
            () => OpenDirectory(paths.DataDirectory),
            () => OpenDirectory(paths.LogsDirectory),
            WinPilotVersion.DisplayVersion,
            () =>
        {
            beginShutdown();
            ExitThread();
        });
        trayIcon = CreateTrayIcon();
        notifyIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "WinPilot",
            Visible = true
        };
        notifyIcon.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Right) trayMenu.Show(Cursor.Position);
        };
        notifyIcon.DoubleClick += (_, _) => OpenWebPage();
        logger.LogInformation("系统托盘已启动");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            logger.LogInformation("系统托盘已停止");
            notifyIcon.Dispose();
            trayIcon.Dispose();
            trayMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OpenWebPage()
    {
        using var process = Process.Start(new ProcessStartInfo(webAddress.AbsoluteUri) { UseShellExecute = true });
    }
    private void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "无法打开目录：{Path}", path);
        }
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        using var iconStream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("WinPilot.Assets.WinPilotIcon.png")
            ?? throw new InvalidOperationException("无法加载系统托盘图标资源");
        using var source = Image.FromStream(iconStream);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(0, 0, 32, 32));

        var iconHandle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally { DestroyIcon(iconHandle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

internal sealed class NativeTrayMenu(
    Action openApplication,
    Func<bool> isSafeModeEnabled,
    Action toggleSafeMode,
    Action openApplicationDirectory,
    Action openDataDirectory,
    Action openLogsDirectory,
    string version,
    Action exitApplication) : NativeWindow, IDisposable
{
    private const uint MfString = 0x00000000;
    private const uint MfChecked = 0x00000008;
    private const uint MfPopup = 0x00000010;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmBottomAlign = 0x0020;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint WmNull = 0x0000;
    private const uint OpenApplicationCommand = 6;
    private const uint VersionCommand = 7;
    private const uint SafeModeCommand = 1;
    private const uint OpenApplicationDirectoryCommand = 2;
    private const uint OpenDataDirectoryCommand = 3;
    private const uint OpenLogsDirectoryCommand = 4;
    private const uint ExitCommand = 5;

    public void Show(Point cursorPosition)
    {
        EnsureHandle();
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        var directoryMenu = CreatePopupMenu();
        if (directoryMenu == IntPtr.Zero)
        {
            DestroyMenu(menu);
            return;
        }
        var moreMenu = CreatePopupMenu();
        if (moreMenu == IntPtr.Zero)
        {
            DestroyMenu(directoryMenu);
            DestroyMenu(menu);
            return;
        }

        try
        {
            AppendMenuW(menu, MfString, (nuint)OpenApplicationCommand, "打开 WinPilot");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString | (isSafeModeEnabled() ? MfChecked : 0), (nuint)SafeModeCommand, "安全模式");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(directoryMenu, MfString, (nuint)OpenApplicationDirectoryCommand, "程序目录");
            AppendMenuW(directoryMenu, MfString, (nuint)OpenDataDirectoryCommand, "数据目录");
            AppendMenuW(directoryMenu, MfString, (nuint)OpenLogsDirectoryCommand, "日志目录");
            AppendMenuW(menu, MfPopup, (nuint)directoryMenu, "打开目录");
            AppendMenuW(moreMenu, MfString, (nuint)VersionCommand, $"版本 {version}");
            AppendMenuW(menu, MfPopup, (nuint)moreMenu, "更多");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, (nuint)ExitCommand, "退出");
            SetForegroundWindow(Handle);
            var command = TrackPopupMenuEx(menu, TpmLeftAlign | TpmBottomAlign | TpmRightButton | TpmReturnCommand, cursorPosition.X, cursorPosition.Y, Handle, IntPtr.Zero);
            if (command == OpenApplicationCommand) openApplication();
            else if (command == SafeModeCommand) toggleSafeMode();
            else if (command == OpenApplicationDirectoryCommand) openApplicationDirectory();
            else if (command == OpenDataDirectoryCommand) openDataDirectory();
            else if (command == OpenLogsDirectoryCommand) openLogsDirectory();
            else if (command == ExitCommand) exitApplication();
        }
        finally
        {
            DestroyMenu(menu);
            PostMessageW(Handle, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dispose() => DestroyHandle();

    private void EnsureHandle()
    {
        if (Handle != IntPtr.Zero) return;
        CreateHandle(new CreateParams { Caption = "WinPilotTrayMenu", Style = 0, ExStyle = 0 });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint itemId, string? text);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
}
