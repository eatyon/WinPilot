using WinPilot.Core.Input;
using WinPilot.Features.Desktop;

namespace WinPilot.Test;

public sealed class KeyboardInputServiceTest
{
    [Fact]
    public void Send_OrdersModifiersBeforeRegularKeys()
    {
        var simulator = new FakeKeyboardInputSimulator();
        var service = new KeyboardInputService(simulator);

        service.Send(["C", "Ctrl", "Shift"]);

        Assert.Equal([KeyboardKey.Ctrl, KeyboardKey.Shift, KeyboardKey.C], simulator.SentKeys);
    }

    [Fact]
    public void Send_RejectsInvalidKeySelections()
    {
        var service = new KeyboardInputService(new FakeKeyboardInputSimulator());

        Assert.Throws<KeyboardInputException>(() => service.Send(["Ctrl", "Shift", "Alt", "S"]));
        Assert.Throws<KeyboardInputException>(() => service.Send(["Ctrl", "Ctrl"]));
        Assert.Throws<KeyboardInputException>(() => service.Send(["Unsupported"]));
        Assert.Throws<KeyboardInputException>(() => service.Send(["0"]));
        Assert.Throws<KeyboardInputException>(() => service.Send(["999"]));
        Assert.Throws<KeyboardInputException>(() => service.Send(["Ctrl, Shift"]));
        Assert.Throws<KeyboardInputException>(() => service.Send(null));
        Assert.Throws<KeyboardInputException>(() => service.Send([]));
        service.Send(["ctrl", "c"]);
        service.Send(["Win", "OemPlus"]);
        service.Send(["CapsLock"]);
        service.Send(["NumLock"]);
        service.Send(["NumpadEnter"]);
    }

    [Theory]
    [InlineData("Shift,Alt,Ctrl", "Ctrl,Alt,Shift")]
    [InlineData("Shift,Ctrl,Win", "Win,Ctrl,Shift")]
    [InlineData("B,Ctrl,A", "Ctrl,B,A")]
    [InlineData("B,A,C", "B,A,C")]
    public void SendUsesFixedModifierOrderAndKeepsNormalKeyOrder(string input, string expected)
    {
        var simulator = new FakeKeyboardInputSimulator();
        new KeyboardInputService(simulator).Send(input.Split(','));
        Assert.Equal(expected.Split(',').Select(Enum.Parse<KeyboardKey>), simulator.SentKeys);
    }

    private sealed class FakeKeyboardInputSimulator : IKeyboardInputSimulator
    {
        public IReadOnlyList<KeyboardKey> SentKeys { get; private set; } = [];

        public void Send(IReadOnlyList<KeyboardKey> keys) => SentKeys = keys.ToArray();
    }
}
