using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace WinPilot.Features.Notifications;

/// <summary>
/// 获取当前进程的通知身份，并在当前用户注册表中登记通知显示名称与缓存图标。
/// </summary>
internal static class WindowsNotificationIdentity
{
    internal static string Register()
    {
        var appId = ResolveAppId();
        using var process = Process.GetCurrentProcess();
        var displayName = GetDisplayName(Assembly.GetEntryAssembly(), process.ProcessName);
        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinPilot",
            "Notifications",
            "Icon.png");
        var iconPath = CacheProgramIcon(Environment.ProcessPath, cachePath);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + appId);
        key.SetValue("DisplayName", displayName);
        if (iconPath is not null)
        {
            key.SetValue("IconUri", iconPath);
        }
        else
        {
            key.DeleteValue("IconUri", throwOnMissingValue: false);
        }
        key.SetValue("IconBackgroundColor", "FFDDDDDD");
        return appId;
    }

    internal static string ResolveAppId()
    {
        var resolver = (IApplicationResolver)Activator.CreateInstance(Type.GetTypeFromCLSID(new("660b90c8-73a9-4b58-8cae-355b7f55341b"), throwOnError: true)!)!;
        string appId;
        try
        {
            resolver.GetAppIDForProcess((uint)Environment.ProcessId, out appId, out _, out _, out _);
        }
        finally
        {
            Marshal.ReleaseComObject(resolver);
        }
        return NormalizeAppId(appId);
    }

    internal static string GetDisplayName(Assembly? assembly, string processName)
        =>
        assembly?.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? processName;

    internal static string? CacheProgramIcon(string? executable, string path)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executable);
            if (icon is null)
            {
                return null;
            }
            using var bitmap = icon.ToBitmap();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    internal static string NormalizeAppId(string appId) =>
        appId.Length > 129
            ? Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(appId)))
            : appId.Replace('\\', '/');

    [ComImport, Guid("de25675a-72de-44b4-9373-05170450c140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationResolver
    {
        void GetAppIDForShortcut(nint shortcut, [MarshalAs(UnmanagedType.LPWStr)] out string appId);

        void GetAppIDForShortcutObject(nint link, nint shortcut, [MarshalAs(UnmanagedType.LPWStr)] out string appId);

        void GetAppIDForWindow(
            nint window,
            [MarshalAs(UnmanagedType.LPWStr)] out string appId,
            out bool preventPinning,
            out bool explicitId,
            out bool validShortcut);

        void GetAppIDForProcess(
            uint processId,
            [MarshalAs(UnmanagedType.LPWStr)] out string appId,
            out bool preventPinning,
            out bool explicitId,
            out bool validShortcut);
    }
}
