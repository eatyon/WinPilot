using System.Reflection;
using Microsoft.AspNetCore.Http;
using WinPilot.Core;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class FileOperationRecoveryTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryFailureRetainsBothOriginalExceptionsInOrder(bool rollback)
    {
        var failure = new IOException("保存失败");
        var recoveryFailure = new UnauthorizedAccessException("收尾失败");
        var error = Assert.Throws<AggregateException>(() => Run(rollback, () => throw failure, () => throw recoveryFailure));
        Assert.Collection(error.InnerExceptions, first => Assert.Same(failure, first), second => Assert.Same(recoveryFailure, second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulRecoveryPreservesOriginalFailure(bool rollback)
    {
        var failure = new IOException("保存失败");
        var recovered = false;
        Assert.Same(failure, Assert.Throws<IOException>(() => Run(rollback, () => throw failure, () => recovered = true)));
        Assert.True(recovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulOperationOnlyRunsUnconditionalCleanup(bool rollback)
    {
        var events = new List<string>();
        Run(rollback, () => events.Add("保存"), () => events.Add("清理"));
        Assert.Equal(rollback ? new[] { "保存" } : new[] { "保存", "清理" }, events);
    }

    [Fact]
    public void CleanupOnlyFailureRemainsVisible()
    {
        var failure = new IOException("清理失败");
        Assert.Same(failure, Assert.Throws<IOException>(() => FileOperationRecovery.WithCleanup(() => { }, () => throw failure)));
    }

    [Fact]
    public void FailedSaveAndCleanupPreserveOriginalFileAndTemporaryFileEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Recovery.Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "config.json");
            var temporary = file + ".tmp";
            File.WriteAllText(file, "original");
            var failure = new IOException("替换失败");
            var cleanupFailure = new IOException("删除失败");
            var error = Assert.Throws<AggregateException>(() => FileOperationRecovery.WithCleanup(() =>
            {
                File.WriteAllText(temporary, "replacement");
                throw failure;
            }, () => throw cleanupFailure));
            Assert.Same(failure, error.InnerExceptions[0]);
            Assert.Same(cleanupFailure, error.InnerExceptions[1]);
            Assert.Equal("original", File.ReadAllText(file));
            Assert.Equal("replacement", File.ReadAllText(temporary));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenameRecoveryPreservesFileContentsAndReportsBlockedRollback(bool blockRollback)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Recovery.Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = Path.Combine(directory, "original.json");
            var renamed = Path.Combine(directory, "renamed.json");
            File.WriteAllText(original, "original");
            File.Move(original, renamed);
            var failure = new IOException("保存失败");
            Exception? rollbackFailure = null;
            var error = Record.Exception(() => FileOperationRecovery.WithRollback(() =>
            {
                if (blockRollback) File.WriteAllText(original, "external");
                throw failure;
            }, () =>
            {
                try { File.Move(renamed, original); }
                catch (Exception exception) { rollbackFailure = exception; throw; }
            }));
            if (blockRollback)
            {
                var aggregate = Assert.IsType<AggregateException>(error);
                Assert.Same(failure, aggregate.InnerExceptions[0]);
                Assert.IsAssignableFrom<IOException>(rollbackFailure);
                Assert.Same(rollbackFailure, aggregate.InnerExceptions[1]);
                Assert.Equal("original", File.ReadAllText(renamed));
                Assert.Equal("external", File.ReadAllText(original));
            }
            else
            {
                Assert.Same(failure, error);
                Assert.Equal("original", File.ReadAllText(original));
                Assert.False(File.Exists(renamed));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(typeof(TaskEndpointRouteBuilderExtensions))]
    [InlineData(typeof(ProgramEndpointRouteBuilderExtensions))]
    [InlineData(typeof(PluginEndpointRouteBuilderExtensions))]
    public void AggregateFailuresAreNotMappedToBusinessErrors(Type endpointType)
    {
        var failure = new AggregateException(new InvalidOperationException("保存失败"), new IOException("回滚失败"));
        var method = endpointType.GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Static)!;
        Func<IResult> action = () => throw failure;
        object?[] arguments = endpointType == typeof(ProgramEndpointRouteBuilderExtensions) ? [action, true] : [action];
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, arguments));
        Assert.Same(failure, error.InnerException);
    }

    private static void Run(bool rollback, Action operation, Action recovery)
    {
        if (rollback) FileOperationRecovery.WithRollback(operation, recovery);
        else FileOperationRecovery.WithCleanup(operation, recovery);
    }
}
