using System.ComponentModel;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class SendInputKeyboardInputSimulatorTest
{
    [Theory]
    [InlineData(0u, 2, 0)]
    [InlineData(0u, 2, 5)]
    [InlineData(1u, 2, 0)]
    [InlineData(1u, 2, 5)]
    public void IncompleteInputIsSystemFailureEvenWhenNativeErrorIsZero(uint sent, int expected, int errorCode)
    {
        var error = Assert.Throws<Win32Exception>(() => SendInputKeyboardInputSimulator.ValidateSentInputCount(sent, expected, errorCode));
        Assert.Equal(errorCode, error.NativeErrorCode);
        Assert.Equal("无法发送键盘输入", error.Message);
    }

    [Theory]
    [InlineData(2u, 2, 0)]
    [InlineData(2u, 2, 5)]
    public void CompleteInputDoesNotTreatStaleErrorCodeAsFailure(uint sent, int expected, int errorCode)
        => SendInputKeyboardInputSimulator.ValidateSentInputCount(sent, expected, errorCode);
}
