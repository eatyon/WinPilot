using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;

namespace WinPilot.Test;

public sealed class NotificationServiceLifetimeTest
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] ChannelFields = ["webhook", "workWeixin", "feishu", "oneBot", "qqbot"];

    [Fact]
    public void ConstructionAndUnusedDisposalDoNotCreateResources()
    {
        using var fixture = new Fixture();
        AssertUnused(fixture.Service);
        fixture.Service.Dispose();
        fixture.Service.Dispose();
        AssertUnused(fixture.Service);
    }

    [Theory]
    [InlineData(null, "请选择发送渠道")]
    [InlineData("", "请选择发送渠道")]
    [InlineData(" ", "请选择发送渠道")]
    [InlineData("all-enabled", "通知渠道无效")]
    [InlineData("unknown", "通知渠道无效")]
    public async Task InvalidChannelDoesNotCreateResources(string? channel, string message)
    {
        using var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendAsync(new("标题", "正文"), channel: channel));
        Assert.Equal(message, error.Message);
        AssertUnused(fixture.Service);
    }

    [Theory]
    [InlineData("Webhook", "webhook")]
    [InlineData("WorkWeixin", "workWeixin")]
    [InlineData("Feishu", "feishu")]
    [InlineData("OneBot", "oneBot")]
    [InlineData("QQBot", "qqbot")]
    public async Task OnlySelectedChannelIsCreatedAndReused(string selected, string field)
    {
        using var fixture = new Fixture();
        Assert.False(Assert.Single(await fixture.Service.SendAsync(new("标题", "正文"), channel: selected)).Succeeded);
        var channel = Read(fixture.Service, field);
        var client = Assert.IsType<HttpClient>(Read(fixture.Service, "httpClient"));
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        Assert.NotNull(channel);
        Assert.All(ChannelFields.Where(name => name != field), name => Assert.Null(Read(fixture.Service, name)));
        await fixture.Service.SendAsync(new("标题", "正文"), channel: selected);
        Assert.Same(channel, Read(fixture.Service, field));
        Assert.Same(client, Read(fixture.Service, "httpClient"));
        fixture.Service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("http://127.0.0.1/"));
        foreach (var name in new[] { "feishu", "qqbot" })
            if (Read(fixture.Service, name) is { } disposableChannel)
            {
                var gate = Assert.IsType<SemaphoreSlim>(disposableChannel.GetType().GetField("tokenGate", PrivateInstance)!.GetValue(disposableChannel));
                Assert.Throws<ObjectDisposedException>(() => gate.Wait(0));
            }
    }

    [Fact]
    public async Task DifferentChannelsReuseOneSharedClient()
    {
        using var fixture = new Fixture();
        await fixture.Service.SendAsync(new("标题", "正文"), channel: "Webhook");
        var client = Read(fixture.Service, "httpClient");
        await fixture.Service.SendAsync(new("标题", "正文"), channel: "QQBot");
        Assert.NotNull(client);
        Assert.Same(client, Read(fixture.Service, "httpClient"));
        Assert.NotNull(Read(fixture.Service, "webhook"));
        Assert.NotNull(Read(fixture.Service, "qqbot"));
        Assert.Null(Read(fixture.Service, "feishu"));
    }

    [Fact]
    public async Task StaticChannelsDoNotCreateSharedNetworkResources()
    {
        using var fixture = new Fixture();
        Assert.False(Assert.Single(await fixture.Service.SendAsync(new("标题", "正文"), channel: "Telegram")).Succeeded);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SendAsync(
            new("标题", "正文"), "Windows", cancellation.Token));
        AssertUnused(fixture.Service);
    }

    [Fact]
    public async Task ConcurrentFirstUseCreatesExactlyOnce()
    {
        using var fixture = new Fixture();
        var getChannel = GetChannelAccessor(fixture.Service);
        object? channel = null;
        var created = 0;
        using var start = new Barrier(9);
        var calls = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(15)));
            return getChannel(ref channel, _ => { Interlocked.Increment(ref created); return new object(); });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(15)));
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, created);
        Assert.All(results, result => Assert.Same(channel, result));
    }

    [Fact]
    public void DisposalBeforeFirstUseDoesNotCreateResources()
    {
        using var fixture = new Fixture();
        var getChannel = GetChannelAccessor(fixture.Service);
        object? channel = null;
        fixture.Service.Dispose();
        Assert.Throws<ObjectDisposedException>(() => getChannel(ref channel, _ => throw new InvalidOperationException("不应调用工厂")));
        AssertUnused(fixture.Service);
    }

    [Fact]
    public async Task DisposalWaitsForCreationAndReleasesCreatedClient()
    {
        using var fixture = new Fixture();
        var getChannel = GetChannelAccessor(fixture.Service);
        object? channel = null;
        HttpClient? client = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var disposal = Task.CompletedTask;
        var creation = Task.Factory.StartNew(() => getChannel(ref channel, value =>
        {
            client = value;
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            return new object();
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            disposal = Task.Factory.StartNew(() =>
            {
                disposing.SetResult();
                fixture.Service.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            release.Set();
            await Task.WhenAll(creation, disposal).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(channel);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client!.GetAsync("http://127.0.0.1/"));
        }
        finally { release.Set(); await Task.WhenAll(creation, disposal); }
    }

    private delegate object ChannelAccessor(ref object? channel, Func<HttpClient, object> create);

    private static ChannelAccessor GetChannelAccessor(NotificationService service) => typeof(NotificationService)
        .GetMethod("GetChannel", PrivateInstance)!.MakeGenericMethod(typeof(object)).CreateDelegate<ChannelAccessor>(service);

    private static object? Read(NotificationService service, string field)
        => typeof(NotificationService).GetField(field, PrivateInstance)!.GetValue(service);

    private static void AssertUnused(NotificationService service)
    {
        Assert.Null(Read(service, "httpClient"));
        Assert.All(ChannelFields, name => Assert.Null(Read(service, name)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("WinPilot-notification-lifetime-").FullName;
        internal NotificationService Service { get; }
        internal Fixture()
        {
            var paths = PortableDataPaths.Create(directory);
            new WinPilotSettings().Save(paths.ConfigurationFile);
            Service = new(paths, NullLogger<NotificationService>.Instance);
        }
        public void Dispose() { Service.Dispose(); Directory.Delete(directory, recursive: true); }
    }
}
