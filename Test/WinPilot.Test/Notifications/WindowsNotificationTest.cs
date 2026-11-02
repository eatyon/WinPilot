using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Windows.UI.Notifications;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;

namespace WinPilot.Test.Notifications;

public sealed class WindowsNotificationTest
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void ContentPreservesTextAndEscapesXml(string newline)
    {
        var xml = XDocument.Parse(WindowsNotificationChannel.CreateContent(new($"标题 <测试> & 中文{newline}正文\n\n第二行")));
        var binding = Assert.Single(xml.Descendants("binding"));
        Assert.Equal("ToastGeneric", (string?)binding.Attribute("template"));
        Assert.Equal(new[] { "标题 <测试> & 中文", "正文\n\n第二行" }, binding.Elements("text").Select(element => element.Value));
        Assert.Empty(binding.Elements("image"));
    }

    [Theory]
    [InlineData("\n正文")]
    [InlineData("标题\n  ")]
    [InlineData("单行内容")]
    public void EmptyTextIsOmitted(string content)
    {
        var xml = XDocument.Parse(WindowsNotificationChannel.CreateContent(new(content)));
        Assert.Single(xml.Descendants("text"));
    }

    [Fact]
    public void ImageRemainsHeroWithAbsoluteFileUri()
    {
        var path = Path.GetTempFileName();
        try
        {
            var xml = XDocument.Parse(WindowsNotificationChannel.CreateContent(new("标题\n正文", path)));
            var image = Assert.Single(xml.Descendants("image"));
            Assert.Equal("hero", (string?)image.Attribute("placement"));
            Assert.Equal(new Uri(Path.GetFullPath(path)).AbsoluteUri, (string?)image.Attribute("src"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingImageFailsBeforeSending()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        var exception = Assert.Throws<FileNotFoundException>(() => WindowsNotificationChannel.CreateContent(new("标题\n正文", path)));
        Assert.Equal(path, exception.FileName);
    }

    [Fact]
    public async Task CancellationDoesNotRegisterOrSend()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsNotificationChannel.SendAsync(new("标题\n正文"), cancellation.Token));
    }

    [Theory]
    [InlineData("WinPilot", "WinPilot")]
    [InlineData("C:\\Tools\\WinPilot.exe", "C:/Tools/WinPilot.exe")]
    public void IdentityUsesExistingToolkitNormalization(string original, string expected)
        =>
        Assert.Equal(expected, WindowsNotificationIdentity.NormalizeAppId(original));

    [Fact]
    public void LongIdentityUsesExistingToolkitHash()
    {
        var original = new string('中', 130);
        Assert.Equal(
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(original))),
            WindowsNotificationIdentity.NormalizeAppId(original));
        Assert.Equal(new string('x', 129), WindowsNotificationIdentity.NormalizeAppId(new string('x', 129)));
    }

    [Fact]
    public void ApplicationIdentityResolutionIsStableWithoutRegistration()
    {
        using var apartment = new NativeWindowsNotification.Apartment();
        var appId = WindowsNotificationIdentity.ResolveAppId();
        Assert.False(string.IsNullOrWhiteSpace(appId));
        Assert.DoesNotContain('\\', appId);
        Assert.InRange(appId.Length, 1, 129);
        Parallel.For(0, 10, _ =>
        {
            using var workerApartment = new NativeWindowsNotification.Apartment();
            Assert.Equal(appId, WindowsNotificationIdentity.ResolveAppId());
        });
    }

    [Fact]
    public void NativeNotificationPreservesContentAndGroupWithoutExpiration()
    {
        using var apartment = new NativeWindowsNotification.Apartment();
        var xml = WindowsNotificationChannel.CreateContent(new("中文 <标题>\n正文 & 内容"));
        using var native = NativeWindowsNotification.Create(xml, "Security.Event", CancellationToken.None);
        var toast = WinRT.MarshalInterface<ToastNotification>.FromAbi(native.Pointer);
        Assert.Equal("Security.Event", toast.Group);
        Assert.Null(toast.ExpirationTime);
        Assert.Equal("", toast.Tag);
        Assert.Equal(XDocument.Parse(xml).ToString(), XDocument.Parse(toast.Content.GetXml()).ToString());
        GC.KeepAlive(toast);
    }

    [Fact]
    public void InvalidXmlDoesNotPreventSubsequentCreation()
    {
        using var apartment = new NativeWindowsNotification.Apartment();
        Assert.ThrowsAny<COMException>(() => NativeWindowsNotification.Create("<toast>", "group", CancellationToken.None));
        using var native = NativeWindowsNotification.Create("<toast/>", "group", CancellationToken.None);
    }

    [Fact]
    public void InvalidGroupDoesNotPreventSubsequentCreation()
    {
        using var apartment = new NativeWindowsNotification.Apartment();
        var groupError = Assert.Throws<COMException>(() => NativeWindowsNotification.Create("<toast/>", new string('x', 100), CancellationToken.None));
        Assert.Equal(unchecked((int)0x803E0209), groupError.HResult);
        using var native = NativeWindowsNotification.Create("<toast/>", "group", CancellationToken.None);
    }

    [Fact]
    public void NativeCreationPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => NativeWindowsNotification.Create("<toast/>", "group", cancellation.Token));
    }

    [Fact]
    public void NativeObjectDisposalIsIdempotent()
    {
        using var apartment = new NativeWindowsNotification.Apartment();
        using var native = NativeWindowsNotification.Create("<toast/>", "group", CancellationToken.None);
        native.Dispose();
        native.Dispose();
        Assert.Throws<ObjectDisposedException>(() => native.Pointer);
    }

    [Fact]
    public void NativeObjectsAreIndependentAcrossThreads()
    {
        Parallel.For(0, 100, _ =>
        {
            using var apartment = new NativeWindowsNotification.Apartment();
            using var native = NativeWindowsNotification.Create("<toast/>", "group", CancellationToken.None);
        });
    }

    [Fact]
    public void ProductionAssemblyDoesNotReferenceNotificationProjectionLibraries()
    {
        var references = typeof(NotificationService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain("Microsoft.Windows.SDK.NET", references);
        Assert.DoesNotContain("WinRT.Runtime", references);
        Assert.DoesNotContain("Microsoft.Toolkit.Uwp.Notifications", references);
    }

    [Fact]
    public void DisplayNameUsesAssemblyTitleThenProcessName()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        Assert.Equal("进程名", WindowsNotificationIdentity.GetDisplayName(assembly, "进程名"));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyTitleAttribute).GetConstructor([typeof(string)])!, ["程序集标题"]));
        Assert.Equal("程序集标题", WindowsNotificationIdentity.GetDisplayName(assembly, "进程名"));
        Assert.Equal("进程名", WindowsNotificationIdentity.GetDisplayName(null, "进程名"));
    }

    [Fact]
    public void ProgramIconIsExtractedAndRefreshed()
    {
        var directory = Directory.CreateTempSubdirectory("WinPilot-toast-").FullName;
        try
        {
            var path = Path.Combine(directory, "cache", "Icon.png");
            Assert.Equal(path, WindowsNotificationIdentity.CacheProgramIcon(Environment.ProcessPath, path));
            using (var image = Image.FromFile(path))
            {
                Assert.True(image.Width > 0 && image.Height > 0);
                Assert.Equal(ImageFormat.Png.Guid, image.RawFormat.Guid);
            }
            File.WriteAllText(path, "旧缓存");
            Assert.Equal(path, WindowsNotificationIdentity.CacheProgramIcon(Environment.ProcessPath, path));
            using var refreshed = Image.FromFile(path);
            Assert.Equal(ImageFormat.Png.Guid, refreshed.RawFormat.Guid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingProgramIconAndCacheFailureAreNonFatal()
    {
        var directory = Directory.CreateTempSubdirectory("WinPilot-toast-").FullName;
        try
        {
            Assert.Null(WindowsNotificationIdentity.CacheProgramIcon(null, Path.Combine(directory, "Icon.png")));
            Assert.Null(WindowsNotificationIdentity.CacheProgramIcon(Path.Combine(directory, "missing.exe"), Path.Combine(directory, "Icon.png")));
            Assert.Null(WindowsNotificationIdentity.CacheProgramIcon(Environment.ProcessPath, directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
