using WinPilot.Core.Input;

namespace WinPilot.Features.Desktop;

public sealed class KeyboardInputService(IKeyboardInputSimulator inputSimulator)
{
    public void Send(IReadOnlyList<string>? keyNames)
    {
        if (keyNames is null || keyNames.Count is < 1 or > 3)
        {
            throw new KeyboardInputException("每次只能发送 1 到 3 个按键");
        }

        var keys = new List<KeyboardKey>(keyNames.Count);
        foreach (var keyName in keyNames)
        {
            if (!Enum.TryParse<KeyboardKey>(keyName, ignoreCase: true, out var key) ||
                !string.Equals(Enum.GetName(key), keyName, StringComparison.OrdinalIgnoreCase))
            {
                throw new KeyboardInputException("包含不支持的按键");
            }

            if (keys.Contains(key))
            {
                throw new KeyboardInputException("按键不能重复");
            }
            keys.Add(key);
        }

        var orderedKeys = keys.OrderBy(key => IsModifier(key) ? 0 : 1).ToArray();
        inputSimulator.Send(orderedKeys);
    }

    private static bool IsModifier(KeyboardKey key) => key is KeyboardKey.Ctrl or KeyboardKey.Shift or KeyboardKey.Alt or KeyboardKey.Win;
}
