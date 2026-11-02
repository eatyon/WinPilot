using System.Runtime.InteropServices;

namespace WinPilot.Features.Notifications;

/// <summary>
/// 通过 WinRT ABI 创建并显示原生通知，封装 COM 引用和 HSTRING 的显式释放。
/// </summary>
internal static unsafe class NativeWindowsNotification
{
    private static readonly Lazy<string> AppId = new(WindowsNotificationIdentity.Register);

    internal static void Send(string xml, string group, CancellationToken token)
    {
        using var apartment = new Apartment();
        using var toast = Create(xml, group, token);
        var appId = AppId.Value;
        token.ThrowIfCancellationRequested();
        using var manager = GetActivationFactory("Windows.UI.Notifications.ToastNotificationManager", new("50ac103f-d235-4598-bbef-98fe4d1a3ad4"));
        using var id = new StringHandle(appId);
        nint notifierPointer = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(manager, 7))(manager.Pointer, id.Pointer, &notifierPointer));
        using var notifier = new ObjectHandle(notifierPointer);
        token.ThrowIfCancellationRequested();
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(notifier, 6))(notifier.Pointer, toast.Pointer));
    }

    internal static ObjectHandle Create(string xml, string group, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var className = new StringHandle("Windows.Data.Xml.Dom.XmlDocument");
        Check(RoActivateInstance(className.Pointer, out var documentPointer));
        using var document = new ObjectHandle(documentPointer);
        using (var io = document.Query(new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637")))
        {
            using (var text = new StringHandle(xml))
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(io, 6))(io.Pointer, text.Pointer));
            }
        }
        using var factory = GetActivationFactory("Windows.UI.Notifications.ToastNotification", new("04124b20-82c6-4229-b109-fd9ed4662b53"));
        nint toastPointer = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(factory, 6))(factory.Pointer, document.Pointer, &toastPointer));
        var toast = new ObjectHandle(toastPointer);
        try
        {
            using var properties = toast.Query(new("9dfb9fd1-143a-490e-90bf-b9fba7132de7"));
            using var groupValue = new StringHandle(group);
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(properties, 8))(properties.Pointer, groupValue.Pointer));
            token.ThrowIfCancellationRequested();
            return toast;
        }
        catch
        {
            toast.Dispose();
            throw;
        }
    }

    internal sealed class Apartment : IDisposable
    {
        private bool owned;

        internal Apartment()
        {
            var result = RoInitialize(1);
            // 当前线程已有不同类型的 COM 单元时沿用现状，不为未成功取得的初始化计数执行反初始化。
            if (result == unchecked((int)0x80010106))
            {
                return;
            }
            Check(result);
            owned = true;
        }

        public void Dispose()
        {
            if (!owned)
            {
                return;
            }
            owned = false;
            RoUninitialize();
        }
    }

    internal sealed class ObjectHandle(nint pointer) : IDisposable
    {
        private nint pointer = pointer != 0 ? pointer : throw new InvalidOperationException("原生通知返回了空对象");

        internal nint Pointer => pointer != 0 ? pointer : throw new ObjectDisposedException(nameof(ObjectHandle));

        internal ObjectHandle Query(Guid iid)
        {
            Check(Marshal.QueryInterface(Pointer, in iid, out var value));
            return new(value);
        }

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref pointer, 0);
            if (value != 0)
            {
                Marshal.Release(value);
            }
        }
    }

    private sealed class StringHandle : IDisposable
    {
        private nint pointer;

        internal nint Pointer => pointer;

        internal StringHandle(string value) => Check(WindowsCreateString(value, checked((uint)value.Length), out pointer));

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref pointer, 0);
            if (value != 0)
            {
                Check(WindowsDeleteString(value));
            }
        }
    }

    private static nint Slot(ObjectHandle value, int index) => (*(nint**)value.Pointer)[index];

    private static void Check(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static ObjectHandle GetActivationFactory(string name, Guid iid)
    {
        using var id = new StringHandle(name);
        Check(RoGetActivationFactory(id.Pointer, in iid, out var value));
        return new(value);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(uint type);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern void RoUninitialize();

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string value,
        uint length,
        out nint result);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint value);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint result);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoActivateInstance(nint name, out nint result);
}
