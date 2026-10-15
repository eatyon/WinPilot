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
    private readonly object gate = new();
    private int activeSends;
    private bool cancellationCompleted;
    private bool released;

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
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            activeSends++;
        }
        var acquired = false;
        try
        {
            await sending.WaitAsync(token);
            acquired = true;
            token.ThrowIfCancellationRequested();
            await send(text, image, ++sequence, token);
        }
        finally
        {
            if (acquired) sending.Release();
            lock (gate) { activeSends--; ReleaseIfIdle(); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { cancellation.Cancel(); }
        finally { lock (gate) { cancellationCompleted = true; ReleaseIfIdle(); } }
    }

    private void ReleaseIfIdle()
    {
        if (disposed == 0 || !cancellationCompleted || activeSends != 0 || released) return;
        released = true;
        cancellation.Dispose();
        sending.Dispose();
    }
}
