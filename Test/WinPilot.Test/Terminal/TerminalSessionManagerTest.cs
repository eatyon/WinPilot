using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Features.Terminal;
using WinPilot.Windows;

namespace WinPilot.Test;

[Collection(TerminalTestCollection.Name)]
public sealed class TerminalSessionManagerTest
{
    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    public async Task ShellReturnsExecutionOutputAndClosingReleasesSession(string shell)
    {
        await using var manager = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var owner = Guid.NewGuid();
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        var expected = first + second;
        var output = new StringBuilder();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Publish(TerminalEvent item)
        {
            if (item.Type == "exited")
                received.TrySetException(new InvalidOperationException($"终端提前退出，退出码：{item.ExitCode}"));
            if (item.Type != "output") return Task.CompletedTask;
            lock (output)
            {
                output.Append(item.Data);
                if (output.ToString().Contains(expected, StringComparison.Ordinal)) received.TrySetResult();
            }
            return Task.CompletedTask;
        }
        var session = await manager.CreateAsync(owner, shell, Path.GetTempPath(), Publish, CancellationToken.None);
        var command = shell == "cmd" ? $"echo {first}^{second}\r" : $"Write-Output ('{first}' + '{second}')\r";
        await manager.InputAsync(owner, session.SessionId, command);
        try
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            lock (output) Assert.Fail($"{shell} 未返回命令结果，终端输出：{output}");
        }
        Assert.Equal(1, manager.ActiveSessionCount);
        await manager.CloseAsync(owner, session.SessionId);
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    [Fact]
    public async Task SessionLimitIsPerConnectionAndClosingOneReleasesCapacity()
    {
        await using var manager = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var owner = Guid.NewGuid();
        var events = new ConcurrentQueue<TerminalEvent>();
        Task Publish(TerminalEvent item) { events.Enqueue(item); return Task.CompletedTask; }
        TerminalSessionInfo first = null!;
        for (var index = 0; index < TerminalSessionManager.MaxSessionsPerConnection; index++)
        {
            var session = await manager.CreateAsync(owner, "cmd", Path.GetTempPath(), Publish, CancellationToken.None);
            if (index == 0) first = session;
        }
        Assert.True(manager.ActiveSessionCount == TerminalSessionManager.MaxSessionsPerConnection,
            $"活动会话：{manager.ActiveSessionCount}，事件：{string.Join("; ", events.Select(item => $"{item.Type}:{item.ExitCode}:{item.Data}"))}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync(owner, "cmd", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None));
        await manager.CreateAsync(Guid.NewGuid(), "cmd", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None);
        await manager.CloseAsync(owner, first.SessionId);
        var replacement = await manager.CreateAsync(owner, "cmd", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None);
        await manager.InputAsync(owner, replacement.SessionId, "");
    }

    [Fact]
    public void NativeAttributeListSizeUsesPointerWidth()
    {
        var method = typeof(ConPtyTerminalProcess).GetMethod("InitializeProcThreadAttributeList",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(typeof(nuint).MakeByRefType(), method.GetParameters()[3].ParameterType);
    }

    [Fact]
    public async Task DisposedManagerRejectsNewSessions()
    {
        await using var manager = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        await manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.CreateAsync(Guid.NewGuid(), "cmd", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    [Fact]
    public async Task ConnectionsCannotOperateEachOthersSessionsAndClosingConnectionLeavesOtherSessionAlive()
    {
        await using var manager = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var first = await manager.CreateAsync(owner, "cmd", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None);
        var second = await manager.CreateAsync(other, "cmd", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InputAsync(other, first.SessionId, ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InterruptAsync(other, first.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ResizeAsync(other, first.SessionId, 80, 25));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CloseAsync(other, first.SessionId));
        await manager.CloseConnectionAsync(owner).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InputAsync(owner, first.SessionId, ""));
        await manager.InputAsync(other, second.SessionId, "");
        await manager.CloseAllAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InputAsync(other, second.SessionId, ""));
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(80, 0)]
    [InlineData(-1, 25)]
    [InlineData(32768, 25)]
    [InlineData(80, 32768)]
    public async Task InvalidDimensionsAreRejectedWithoutClosingSession(int columns, int rows)
    {
        await using var manager = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var owner = Guid.NewGuid();
        var session = await manager.CreateAsync(owner, "cmd", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.ResizeAsync(owner, session.SessionId, columns, rows));
        await manager.ResizeAsync(owner, session.SessionId, 80, 25);
    }

    [Fact]
    public async Task DisposingManagerClosesAllSessionsAndCanBeRepeated()
    {
        var manager = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        await using (manager)
        {
            var owner = Guid.NewGuid();
            var session = await manager.CreateAsync(owner, "cmd", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None);
            await manager.DisposeAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InputAsync(owner, session.SessionId, ""));
        }
    }

    [Fact]
    public async Task InvalidShellMissingDirectoryAndCancellationDoNotCreateSession()
    {
        await using var manager = new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance);
        var owner = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync(owner, "unsupported", Path.GetTempPath(), _ => Task.CompletedTask, CancellationToken.None));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => manager.CreateAsync(owner, "cmd", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => Task.CompletedTask, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.CreateAsync(owner, "cmd", Path.GetTempPath(), _ => Task.CompletedTask, new CancellationToken(true)));
        await manager.CloseConnectionAsync(owner);
    }
}
