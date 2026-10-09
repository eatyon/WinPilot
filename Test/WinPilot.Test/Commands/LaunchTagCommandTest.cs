using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Programs;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.Features.Programs;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace WinPilot.Test;

public sealed class LaunchTagCommandTest
{
    [Theory]
    [InlineData("启动标签")]
    [InlineData("启动标签 Chrome")]
    [InlineData("启动标签Chrome")]
    [InlineData(" 启动标签 我的 游戏 ")]
    public void ListAndArgumentsAreRecognized(string text) => Assert.True(CommandProcessor.IsSupportedCommand(text));

    [Fact]
    public async Task ImageFailureFallsBackToOnlyTagNamesAndPrefixedUsageWithoutLaunching()
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("Chrome"));
        fixture.Tags.Create(Tag("我的 游戏"));
        using var processor = fixture.Processor();
        var replies = new List<string>();
        await processor.ProcessAsync(Message("启动标签", text => replies.Add(text), failImage: true), "/", CancellationToken.None);
        var reply = Assert.Single(replies);
        Assert.Contains("Chrome", reply);
        Assert.Contains("我的 游戏", reply);
        Assert.Contains("/启动标签 标签名", reply);
        Assert.DoesNotContain("示例：", reply);
        Assert.Contains("空格可省略", reply);
        Assert.DoesNotContain("C:\\stored", reply);
        Assert.Empty(fixture.Programs.Requests);
    }

    [Fact]
    public async Task EmptyListGivesSetupAndUsageInstructions()
    {
        using var fixture = new Fixture();
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("启动标签", text => replies.Add(text)), "!", CancellationToken.None);
        var reply = Assert.Single(replies);
        Assert.Contains("暂无已保存的标签", reply);
        Assert.Contains("!启动标签 标签名", reply);
        Assert.DoesNotContain("空格可省略", reply);
        Assert.Empty(fixture.Programs.Requests);
    }

    [Theory]
    [InlineData("启动标签 Chrome")]
    [InlineData("启动标签Chrome")]
    [InlineData("启动标签 chrome")]
    [InlineData("启动标签\tChrome ")]
    public async Task ValidTagLaunchesExactlyOnceWithSavedOptionsBeforeSingleReply(string command)
    {
        using var fixture = new Fixture();
        var tag = Tag("Chrome");
        fixture.Tags.Create(tag);
        var steps = new List<string>();
        fixture.Programs.OnLaunch = _ => steps.Add("launch");
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message(command, text => steps.Add(text)), "", CancellationToken.None);
        Assert.Equal(2, steps.Count);
        Assert.Equal("launch", steps[0]);
        Assert.Equal("已提交「启动标签 Chrome」命令", steps[1]);
        Assert.Equal(new ProgramLaunchRequest(tag.ProgramPath, tag.Arguments, tag.WorkingDirectory, tag.RunElevated, tag.Silent), Assert.Single(fixture.Programs.Requests));
    }

    [Fact]
    public async Task SpacesInsideTagNameArePreserved()
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("我的 游戏"));
        using var processor = fixture.Processor();
        var replies = new List<string>();
        await processor.ProcessAsync(Message("启动标签我的 游戏", text => replies.Add(text)), "", CancellationToken.None);
        Assert.Contains("我的 游戏", Assert.Single(replies));
        Assert.Single(fixture.Programs.Requests);
    }

    [Fact]
    public async Task UnknownTagAndExtraArgumentsDoNotLaunch()
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("Chrome"));
        using var processor = fixture.Processor();
        var replies = new List<string>();
        await processor.ProcessAsync(Message("启动标签Chrome --extra", text => replies.Add(text)), "/", CancellationToken.None);
        Assert.Contains("未找到标签", Assert.Single(replies));
        Assert.Contains("/启动标签", replies[0]);
        Assert.Empty(fixture.Programs.Requests);
    }

    [Theory]
    [InlineData("file", "程序文件不存在")]
    [InlineData("directory", "起始目录不存在")]
    [InlineData("permission", "没有启动权限")]
    [InlineData("windows", "Windows 无法启动该程序，请检查文件和权限")]
    [InlineData("other", "请检查标签配置和程序状态")]
    public async Task ImmediateLaunchFailureRepliesOnceWithSafeReason(string failure, string reason)
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("Chrome"));
        fixture.Programs.OnLaunch = _ => throw (failure switch
        {
            "file" => new FileNotFoundException("secret-path"),
            "directory" => new DirectoryNotFoundException("secret-path"),
            "permission" => new UnauthorizedAccessException("secret-path"),
            "windows" => new System.ComponentModel.Win32Exception("secret-path"),
            _ => new InvalidOperationException("secret-path")
        });
        using var processor = fixture.Processor();
        var replies = new List<string>();
        await processor.ProcessAsync(Message("启动标签Chrome", text => replies.Add(text)), "", CancellationToken.None);
        Assert.Equal($"无法提交「启动标签 Chrome」命令：{reason}", Assert.Single(replies));
        Assert.DoesNotContain("secret-path", replies[0]);
        Assert.Single(fixture.Programs.Requests);
    }

    [Fact]
    public async Task ReplyFailureDoesNotRepeatExecution()
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("Chrome"));
        using var processor = fixture.Processor();
        var replies = 0;
        await processor.ProcessAsync(Message("启动标签Chrome", _ => { replies++; throw new IOException(); }), "", CancellationToken.None);
        Assert.Equal(1, replies);
        Assert.Single(fixture.Programs.Requests);
    }

    [Fact]
    public async Task CancellationAfterReplyDoesNotUndoExecution()
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("Chrome"));
        using var processor = fixture.Processor();
        using var cancellation = new CancellationTokenSource();
        var replies = 0;
        await processor.ProcessAsync(Message("启动标签Chrome", _ => { replies++; cancellation.Cancel(); }), "", cancellation.Token);
        Assert.Equal(1, replies);
        Assert.Single(fixture.Programs.Requests);
    }

    [Fact]
    public async Task LaunchAcknowledgementUsesSingleSequence()
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("Chrome"));
        var sequences = new List<int>();
        await new LaunchTagCommand(fixture.Tags, fixture.Programs, NullLogger.Instance, "Test").ExecuteAsync("Chrome", "",
            (_, sequence) => { sequences.Add(sequence); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(new[] { 1 }, sequences);
        Assert.Single(fixture.Programs.Requests);
    }

    [Fact]
    public async Task CorruptConfigurationDoesNotLaunchOrLeakFilePaths()
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("Chrome"));
        File.WriteAllText(Path.Combine(fixture.Paths.TagsDirectory, "broken.json"), "{");
        var replies = new List<string>();
        using var processor = fixture.Processor();
        await processor.ProcessAsync(Message("启动标签Chrome", text => replies.Add(text)), "", CancellationToken.None);
        Assert.Contains("无法读取标签", Assert.Single(replies));
        Assert.DoesNotContain(fixture.Paths.ApplicationDirectory, replies[0]);
        Assert.Empty(fixture.Programs.Requests);
    }

    [Fact]
    public async Task OneBotReceptionDispatchesTagCommandAndRepliesOnceAfterLaunch()
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("Chrome"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Map("/", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    post_type = "message", message_type = "group", self_id = "bot", user_id = "user", group_id = "group",
                    message_id = "command", message = "/启动标签Chrome"
                }), WebSocketMessageType.Text, true, context.RequestAborted);
                var buffer = new byte[65536];
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                using var json = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                var payload = json.RootElement;
                Assert.Equal("send_msg", payload.GetProperty("action").GetString());
                var parameters = payload.GetProperty("params");
                Assert.Equal("group", parameters.GetProperty("group_id").GetString());
                var text = parameters.GetProperty("message")[0].GetProperty("data").GetProperty("text").GetString();
                Assert.Equal("已提交「启动标签 Chrome」命令", text);
                Assert.Single(fixture.Programs.Requests);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { status = "ok", retcode = 0, echo = payload.GetProperty("echo").GetString() }),
                    WebSocketMessageType.Text, true, context.RequestAborted);
                completed.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (Exception exception)
            {
                if (!completed.Task.IsCompleted) completed.TrySetException(exception);
            }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("http://", "ws://");
        new WinPilotSettings
        {
            Port = 9123, SafeModeEnabled = true,
            Notifications = new() { OneBotUserId = "user", OneBotGroupId = "group", OneBotSendTarget = "private" },
            CommandBots = new() { OneBotEnabled = true, OneBotWebSocketUrl = address, OneBotPrefix = "/" }
        }.Save(fixture.Paths.ConfigurationFile);
        using var configuration = new CommandBotConfiguration();
        await using var bot = new OneBotCommandBotService(fixture.Paths, NullLogger<OneBotCommandBotService>.Instance,
            configuration, tags: fixture.Tags, programs: fixture.Programs);
        await bot.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Programs.Requests);
    }

    private static ProgramTag Tag(string name) => new(name, "C:\\stored\\app.exe", "--saved", "C:\\stored", true, true);

    [Fact]
    public async Task BareCommandSendsOnlyImageWithoutLaunching()
    {
        using var fixture = new Fixture();
        fixture.Tags.Create(Tag("Chrome"));
        using var processor = fixture.Processor();
        var images = new List<byte[]>();
        await processor.ProcessAsync(new("origin", "sender", "启动标签")
        {
            Reply = (text, image, _) =>
            {
                Assert.Equal("", text);
                images.Add(Assert.IsType<byte[]>(image));
                return Task.CompletedTask;
            }
        }, "/", CancellationToken.None);
        Assert.Single(images);
        Assert.Empty(fixture.Programs.Requests);
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

    private sealed class Fixture : IDisposable
    {
        internal PortableDataPaths Paths { get; } = PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), "winpilot-launch-tag-tests", Guid.NewGuid().ToString("N")));
        internal ProgramTagService Tags { get; }
        internal ProgramManager Programs { get; } = new();
        internal Fixture() => Tags = new(Paths);
        internal CommandProcessor Processor() => new("Test", new(), NullLogger.Instance, tags: Tags, programs: Programs);
        public void Dispose() { if (Directory.Exists(Paths.ApplicationDirectory)) Directory.Delete(Paths.ApplicationDirectory, true); }
    }

    private sealed class ProgramManager : IProgramManager
    {
        internal readonly List<ProgramLaunchRequest> Requests = [];
        internal Action<ProgramLaunchRequest>? OnLaunch;
        public void Launch(ProgramLaunchRequest request) { Requests.Add(request); OnLaunch?.Invoke(request); }
        public IReadOnlyList<InteractiveUser> GetInteractiveUsers() => throw new NotSupportedException();
        public IReadOnlyList<RunningProgram> GetRunningPrograms(string userName) => throw new NotSupportedException();
        public ProgramUsage GetUsage(IReadOnlyList<int> ids, CancellationToken token) => throw new NotSupportedException();
        public void Close(IReadOnlyList<int> ids, bool force) => throw new NotSupportedException();
    }
}
