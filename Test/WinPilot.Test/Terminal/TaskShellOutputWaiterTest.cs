using System.Text.RegularExpressions;
using WinPilot.Features.Terminal;

namespace WinPilot.Test;

public sealed class TaskShellOutputWaiterTest
{
    [Theory]
    [InlineData("cmd", "CMD")]
    [InlineData("powershell", "PowerShell")]
    public async Task ChunkedMarkersAndAnsiSequencesDoNotCountAsCommandOutput(string shell, string expectedShell)
    {
        using var waiter = new TaskShellOutputWaiter(shell, "echo value");
        var begin = Regex.Match(waiter.WrappedCommand, "__WINPILOT_BEGIN_[a-f0-9]+__").Value;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = waiter.WaitAsync(cancellation.Token);
        var prefix = "prompt echo " + begin + "\r\n\x1b[32m" + begin + "\x1b[0m\r\n";
        foreach (var character in prefix) waiter.Observe(new("output", null, "stdout", character.ToString()));
        Assert.False(waiting.IsCompleted);
        waiter.Observe(new("output", null, "stdout", "value"));
        var result = await waiting;
        Assert.Equal(expectedShell, result.Shell);
        Assert.True(result.ReceivedOutput);
    }

    [Fact]
    public async Task EmptyCompletedCommandStillWaitsForCancellationAndDisposeIgnoresLateOutput()
    {
        using var waiter = new TaskShellOutputWaiter("cmd", "echo");
        var begin = Regex.Match(waiter.WrappedCommand, "__WINPILOT_BEGIN_[a-f0-9]+__").Value;
        var end = Regex.Match(waiter.WrappedCommand, "__WINPILOT_END_[a-f0-9]+__").Value;
        using var cancellation = new CancellationTokenSource();
        var waiting = waiter.WaitAsync(cancellation.Token);
        waiter.Observe(new("output", null, "stdout", begin + "\r\n" + end + "\r\n"));
        Assert.False(waiting.IsCompleted);
        waiter.Dispose();
        waiter.Observe(new("output", null, "stdout", "late"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
