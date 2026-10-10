using Microsoft.Extensions.Logging;

namespace WinPilot.Test;

internal class CommandBotTestObserver : ILogger
{
    private int count;
    internal int Count => Volatile.Read(ref count);
    internal System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        Entries.Enqueue((logLevel, message, exception));
        if (logLevel == LogLevel.Debug && message.Contains("命令机器人收到消息：", StringComparison.Ordinal))
            Interlocked.Increment(ref count);
    }

    internal static async Task<int> NextCommandAsync(CommandBotTestObserver observer, CancellationToken token = default)
    {
        var previous = observer.Count;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            var received = observer.Count;
            if (received != previous) return received;
            await Task.Delay(10, timeout.Token);
        }
    }
}

internal sealed class CommandBotTestObserver<T> : CommandBotTestObserver, ILogger<T>;
