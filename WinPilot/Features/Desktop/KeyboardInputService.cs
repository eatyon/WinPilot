using WinPilot.Core.Input;

namespace WinPilot.Features.Desktop;

/// <summary>
/// 校验一到三个不重复按键，并按修饰键优先的顺序交给平台输入模拟器。
/// </summary>
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
            if (!Enum.TryParse<KeyboardKey>(keyName, ignoreCase: true, out var key)
                || !string.Equals(Enum.GetName(key), keyName, StringComparison.OrdinalIgnoreCase))
            {
                throw new KeyboardInputException("包含不支持的按键");
            }

            if (keys.Contains(key))
            {
                throw new KeyboardInputException("按键不能重复");
            }
            keys.Add(key);
        }

        var orderedKeys = keys.OrderBy(KeyOrder).ToArray();
        inputSimulator.Send(orderedKeys);
    }

    internal static string[]? OrderKeyNames(IReadOnlyList<string>? names) =>
        names?.OrderBy(name =>
            Enum.TryParse<KeyboardKey>(name, true, out var key) ? KeyOrder(key) : 4).ToArray();

    private static int KeyOrder(KeyboardKey key) =>
        key switch
        {
            KeyboardKey.Win => 0,
            KeyboardKey.Ctrl => 1,
            KeyboardKey.Alt => 2,
            KeyboardKey.Shift => 3,
            _ => 4
        };
}
