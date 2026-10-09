namespace WinPilot.Core.Input;

public interface IKeyboardInputSimulator
{
    void Send(IReadOnlyList<KeyboardKey> keys);
}
