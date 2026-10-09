using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinPilot.Test;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TerminalTestCollection : ICollectionFixture<TerminalTestEnvironment>
{
    public const string Name = "Terminal";
}

public sealed class TerminalTestEnvironment : IDisposable
{
    private readonly string? modulePath = Environment.GetEnvironmentVariable("PSModulePath");
    private readonly (int Id, IntPtr Handle)[] handles = new[] { -10, -11, -12 }
        .Select(id => (id, GetStdHandle(id))).ToArray();

    public TerminalTestEnvironment()
    {
        try
        {
            Environment.SetEnvironmentVariable("PSModulePath", null);
            foreach (var handle in handles)
                if (!SetStdHandle(handle.Id, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PSModulePath", modulePath);
        Win32Exception? error = null;
        foreach (var handle in handles)
            if (!SetStdHandle(handle.Id, handle.Handle))
                error ??= new Win32Exception(Marshal.GetLastWin32Error());
        if (error is not null) throw error;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int id);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int id, IntPtr handle);
}
