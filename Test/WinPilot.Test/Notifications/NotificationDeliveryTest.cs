using Microsoft.Extensions.Logging;
using WinPilot.Core.Notifications;
using WinPilot.Features.Notifications;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class NotificationDeliveryTest
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task CleanupFailureDoesNotReplaceDeliveryResult(string kind)
    {
        var logger = new RecordingLogger();
        var notifications = new LockedImageNotifications(kind);
        try
        {
            var delivery = NotificationDelivery.SendAsync(notifications, new("title", "body"), CancellationToken.None,
                imageContent: [1], imageContentType: "image/png", logger: logger);
            if (kind == "success") await delivery;
            else Assert.Same(notifications.Failure, await Assert.ThrowsAnyAsync<Exception>(() => delivery));
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains(notifications.Path!, entry.Message);
            Assert.IsAssignableFrom<IOException>(entry.Exception);
            Assert.True(File.Exists(notifications.Path));
        }
        finally
        {
            notifications.Lock?.Dispose();
            if (notifications.Path is not null) File.Delete(notifications.Path);
        }
    }

    [Fact]
    public void SaveFailureClassificationDoesNotAbsorbUnrelatedAggregates()
    {
        Assert.True(CommandBotEndpointRouteBuilderExtensions.IsSaveFailure(new IOException()));
        Assert.True(CommandBotEndpointRouteBuilderExtensions.IsSaveFailure(new UnauthorizedAccessException()));
        Assert.True(CommandBotEndpointRouteBuilderExtensions.IsSaveFailure(new AggregateException(new IOException(), new UnauthorizedAccessException())));
        Assert.False(CommandBotEndpointRouteBuilderExtensions.IsSaveFailure(new AggregateException(new IOException(), new InvalidOperationException())));
        Assert.False(CommandBotEndpointRouteBuilderExtensions.IsSaveFailure(new AggregateException(new IOException(), new OperationCanceledException())));
    }

    [Fact]
    public async Task NormalTemporaryCleanupAndLocalFileOwnershipStayUnchanged()
    {
        var logger = new RecordingLogger();
        var notifications = new LockedImageNotifications("success", false);
        await NotificationDelivery.SendAsync(notifications, new("title", "body"), CancellationToken.None,
            imageContent: [1], imageContentType: "image/png", logger: logger);
        Assert.False(File.Exists(notifications.Path));
        Assert.Empty(logger.Entries);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinPilot-notification-test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await File.WriteAllBytesAsync(path, [1]);
            await NotificationDelivery.SendAsync(notifications, new("title", "body", path), CancellationToken.None, logger: logger);
            Assert.True(File.Exists(path));
            Assert.Empty(logger.Entries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingTemporaryFileIsNotACleanupFailure()
    {
        var logger = new RecordingLogger();
        NotificationDelivery.DeleteTemporaryImage(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), logger);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialWriteCleanupFailurePreservesWriteOrCancellationException(bool cancelled)
    {
        var logger = new RecordingLogger();
        Exception failure = cancelled ? new OperationCanceledException("write cancelled") : new IOException("write failed");
        string? path = null;
        FileStream? locked = null;
        try
        {
            var writing = NotificationDelivery.WriteTemporaryImageAsync([1], "image/png", CancellationToken.None, logger,
                async (target, content, token) =>
                {
                    path = target;
                    await File.WriteAllBytesAsync(target, content, token);
                    locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw failure;
                });
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => writing));
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.IsAssignableFrom<IOException>(entry.Exception);
            Assert.Contains(path!, entry.Message);
            Assert.True(File.Exists(path));
        }
        finally
        {
            locked?.Dispose();
            if (path is not null) File.Delete(path);
        }
    }

    private sealed class LockedImageNotifications(string kind, bool lockImage = true) : INotificationService
    {
        internal FileStream? Lock;
        internal string? Path;
        internal Exception Failure { get; } = kind == "cancel" ? new OperationCanceledException() : new IOException("delivery failure");
        public Task<IReadOnlyList<NotificationChannelResult>> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default,
            string? channel = null, bool ignoreChannelEnabled = false)
        {
            Path = message.ImagePath;
            if (lockImage) Lock = new FileStream(Path!, FileMode.Open, FileAccess.Read, FileShare.Read);
            return kind == "success" ? Task.FromResult<IReadOnlyList<NotificationChannelResult>>([new("Test", true)])
                : Task.FromException<IReadOnlyList<NotificationChannelResult>>(Failure);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        internal List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, exception), exception));
    }
}
