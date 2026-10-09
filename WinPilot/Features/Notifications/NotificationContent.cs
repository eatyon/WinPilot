using System.Drawing.Imaging;
using System.Net.Http.Headers;
using WinPilot.Core.Notifications;

namespace WinPilot.Features.Notifications;

internal static class NotificationContent
{
    internal static string FormatMessage(NotificationMessage message) =>
        string.Join("\n", new[] { message.Title, message.Content }.Where(text => !string.IsNullOrWhiteSpace(text)));

    internal static async Task<byte[]> ReadPngBytesAsync(string path, CancellationToken token) =>
        await ReadImageBytesAsync(path, ImageFormat.Png, token);

    internal static async Task<byte[]?> ReadJpegBytesAsync(string? path, CancellationToken token) =>
        string.IsNullOrWhiteSpace(path) ? null : await ReadImageBytesAsync(path, ImageFormat.Jpeg, token);

    internal static async Task<string?> ReadJpegBase64Async(string? path, CancellationToken token)
    {
        var bytes = await ReadJpegBytesAsync(path, token);
        return bytes is null ? null : Convert.ToBase64String(bytes);
    }

    internal static byte[] ToJpegBytes(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var image = Image.FromStream(input);
        if (image.RawFormat.Guid == ImageFormat.Jpeg.Guid) return bytes;
        using var bitmap = new Bitmap(image);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Jpeg);
        return output.ToArray();
    }

    private static bool IsJpeg(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8;

    internal static string ImageFileName(byte[] bytes) => IsJpeg(bytes) ? "image.jpg" : "image.png";

    internal static ByteArrayContent CreateImageContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(IsJpeg(bytes) ? "image/jpeg" : "image/png");
        return content;
    }

    private static async Task<byte[]> ReadImageBytesAsync(string path, ImageFormat format, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var input = File.OpenRead(path);
        using var image = Image.FromStream(input);
        using var bitmap = new Bitmap(image);
        using var output = new MemoryStream();
        bitmap.Save(output, format);
        return output.ToArray();
    }
}
