using WinPilot.Core.Tasks;
using WinPilot.Features.Notifications.CommandBots;

namespace WinPilot.Test;

public sealed class RunTaskCommandTest
{
    [Fact]
    public async Task EmptyListDoesNotShowOptionalSpaceHint()
    {
        await using var fixture = new TaskServiceFixture();
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("运行任务", replies.Add), "/", CancellationToken.None);
        var reply = Assert.Single(replies);
        Assert.Contains("暂无已保存的任务", reply);
        Assert.Contains("/运行任务 任务名", reply);
        Assert.DoesNotContain("空格可省略", reply);
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Theory]
    [InlineData("运行任务 Example")]
    [InlineData("运行任务example")]
    [InlineData(" 运行任务 Example ")]
    public async Task RunsSavedTaskAndRepliesOnce(string command)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message(command, text =>
        {
            Assert.Equal(1, fixture.Programs.Launches);
            replies.Add(text);
        }), "/", CancellationToken.None);
        Assert.Equal("已提交「运行任务 Example」命令", Assert.Single(replies));
        Assert.Equal(1, fixture.Programs.Launches);
    }

    [Fact]
    public async Task ImageFailureFallsBackToAllTaskNamesWithoutEnabledState()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("可运行");
        fixture.Create("已关闭", false);
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("运行任务", replies.Add, failImage: true), "/", CancellationToken.None);
        var text = Assert.Single(replies);
        Assert.Contains("已关闭", text);
        Assert.DoesNotContain("（已禁用）", text);
        Assert.Contains("/运行任务 任务名", text);
        Assert.DoesNotContain("示例：", text);
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Fact]
    public async Task UnknownTaskDoesNotExecute()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("已关闭", false);
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("运行任务不存在", replies.Add), "", CancellationToken.None);
        Assert.Single(replies);
        Assert.Equal(0, fixture.Programs.Launches);
        Assert.False(Assert.Single(fixture.Tasks.GetAll()).Enabled);
    }

    [Fact]
    public async Task DisabledTaskCanRunManuallyWithoutEnablingIt()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("已关闭", false);
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("运行任务已关闭", replies.Add), "", CancellationToken.None);
        Assert.Equal("已提交「运行任务 已关闭」命令", Assert.Single(replies));
        Assert.Equal(1, fixture.Programs.Launches);
        Assert.False(Assert.Single(fixture.Tasks.GetAll()).Enabled);
    }

    [Fact]
    public async Task FailedReplyDoesNotRepeatExecution()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        var replies = 0;
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("运行任务Example", _ => { replies++; throw new IOException(); }), "", CancellationToken.None);
        Assert.Equal(1, fixture.Programs.Launches);
        Assert.Equal(1, replies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecutionFailureIsNotReportedAndPreservesContinueOnFailure(bool continueOnFailure)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Programs.FailFirst = true;
        fixture.Tasks.Create(new("Example", true, new(TaskTriggerType.WinPilotStartup),
            [new(TaskActionType.StartProgram, ProgramPath: "app.exe", ContinueOnFailure: continueOnFailure),
             new(TaskActionType.StartProgram, ProgramPath: "app.exe")]));
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("运行任务Example", replies.Add), "", CancellationToken.None);
        Assert.Equal(continueOnFailure ? 2 : 1, fixture.Programs.Launches);
        Assert.Equal("已提交「运行任务 Example」命令", Assert.Single(replies));
    }

    [Fact]
    public async Task ReplyDoesNotWaitForCompletionOrSendFinalResult()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example", delay: true);
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("运行任务Example", replies.Add), "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(replies);
        Assert.Equal(0, fixture.Programs.Launches);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(replies);
        Assert.Equal(1, fixture.Programs.Launches);
    }

    [Fact]
    public async Task ConcurrentRunKeepsExistingDuplicateSuppression()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example", delay: true);
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("运行任务Example", replies.Add), "", CancellationToken.None);
        await processor.ProcessAsync(Message("运行任务Example", replies.Add), "", CancellationToken.None);
        await fixture.Programs.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, replies.Count);
        Assert.All(replies, text => Assert.Equal("已提交「运行任务 Example」命令", text));
        Assert.Equal(1, fixture.Programs.Launches);
    }

    [Fact]
    public async Task CancelledCommandDoesNotRun()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("Example");
        using var processor = fixture.Processor();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(Message("运行任务Example", _ => throw new InvalidOperationException()), "", new CancellationToken(true)));
        Assert.Equal(0, fixture.Programs.Launches);
    }

    [Fact]
    public async Task BareCommandSendsOnlyImageWithoutRunningTasks()
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Create("已关闭", false);
        using var processor = fixture.Processor();
        var images = new List<byte[]>();
        await processor.ProcessAsync(new("origin", "sender", "运行任务")
        {
            Reply = (text, image, _) =>
            {
                Assert.Equal("", text);
                images.Add(Assert.IsType<byte[]>(image));
                return Task.CompletedTask;
            }
        }, "", CancellationToken.None);
        Assert.Single(images);
        Assert.Equal(0, fixture.Programs.Launches);
    }

    private static CommandBotMessage Message(string command, Action<string> receive, bool failImage = false) => new("origin", "sender", command)
    {
        Reply = (text, image, _) =>
        {
            if (failImage && image is not null) throw new IOException("模拟图片发送失败");
            Assert.Null(image);
            receive(text);
            return Task.CompletedTask;
        }
    };
}
