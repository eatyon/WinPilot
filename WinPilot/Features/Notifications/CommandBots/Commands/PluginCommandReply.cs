using WinPilot.PluginContract;

namespace WinPilot.Features.Notifications.CommandBots.Commands;

internal sealed class PluginCommandReply : IPluginCommandReply, IDisposable
{
    private readonly Func<string, byte[]?, int, CancellationToken, Task> send;
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationToken token;
    private readonly SemaphoreSlim sending = new(1, 1);
    private int sequence;
    private int disposed;

    internal PluginCommandReply(Func<string, byte[]?, int, CancellationToken, Task> send, CancellationToken token)
    {
        this.send = send;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        this.token = cancellation.Token;
    }

    public Task ReplyTextAsync(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return ReplyAsync(text, null);
    }

    public Task ReplyImageAsync(PluginImageContent image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Content is null || image.Content.Length == 0 || image.ContentType is not ("image/png" or "image/jpeg"))
            throw new ArgumentException("命令回复图片必须是非空 PNG 或 JPEG", nameof(image));
        return ReplyAsync("", image.Content);
    }

    private async Task ReplyAsync(string text, byte[]? image)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await sending.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await send(text, image, ++sequence, token);
        }
        finally { sending.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }
}
