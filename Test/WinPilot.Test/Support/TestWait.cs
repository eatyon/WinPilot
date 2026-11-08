using System.Runtime.CompilerServices;

namespace WinPilot.Test;

internal static class TestWait
{
    internal static async Task UntilAsync(
        Func<bool> condition,
        int timeoutSeconds = 8,
        int pollMilliseconds = 10,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            while (!condition())
            {
                await Task.Delay(pollMilliseconds, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"等待条件超时（{timeoutSeconds} 秒）：{expression}");
        }
    }
}
