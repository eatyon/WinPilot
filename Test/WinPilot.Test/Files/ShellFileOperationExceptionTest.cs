using WinPilot.Features.Files;

namespace WinPilot.Test;

public sealed class ShellFileOperationExceptionTest
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(5, false)]
    [InlineData(32, true)]
    [InlineData(0x78, false)]
    [InlineData(0x10074, false)]
    [InlineData(0x10074, true)]
    [InlineData(0x12345678, false)]
    [InlineData(int.MinValue, true)]
    public void PreservesCompleteReturnCodeAndAbortFlagWithoutChangingIOExceptionHandling(int returnCode, bool aborted)
    {
        const string message = "原有错误文案";
        Exception failure = new ShellFileOperationException(message, returnCode, aborted);
        var caught = Assert.ThrowsAny<IOException>((Action)(() => throw failure));
        var operation = Assert.IsType<ShellFileOperationException>(caught);
        Assert.Same(failure, caught);
        Assert.Equal(returnCode, operation.ReturnCode);
        Assert.Equal(aborted, operation.AnyOperationsAborted);
        Assert.Equal(message, operation.Message);
        Assert.Null(operation.InnerException);
        Assert.False(failure is OperationCanceledException);
    }
}
