using WinPilot.Core.Input;

namespace WinPilot.Features.Tasks;

internal sealed record KeyboardTriggerBinding(object Owner, IReadOnlyList<KeyboardKey> Keys);
internal readonly record struct KeyboardInputIdentity(int ScanCode, int VirtualKey, bool Extended)
{
    internal static KeyboardInputIdentity FromScanCode(int scanCode, int virtualKey, bool extended) =>
        new(scanCode, scanCode == 0 ? virtualKey : 0, extended);
}

internal sealed class KeyboardTriggerMatcher
{
    private readonly Dictionary<KeyboardInputIdentity, KeyboardKey?> pressed = [];
    private readonly Dictionary<KeyboardKey, int> keyCounts = [];
    private Rule[] rules = [];

    internal void Update(IReadOnlyList<KeyboardTriggerBinding> bindings, long activatedAt = 0)
    {
        rules = bindings.Select(binding => rules.FirstOrDefault(rule => ReferenceEquals(rule.Binding, binding))
            ?? new Rule(binding) { Blocked = pressed.Count != 0, ActivatedAt = activatedAt }).ToArray();
    }

    internal void Reset(long? activatedAt = null)
    {
        pressed.Clear();
        keyCounts.Clear();
        foreach (var rule in rules)
        {
            rule.Progress = 0;
            rule.Blocked = false;
            if (activatedAt is { } timestamp) rule.ActivatedAt = timestamp;
        }
    }

    internal IReadOnlyList<KeyboardTriggerBinding> Input(KeyboardInputIdentity identity, KeyboardKey? key, bool down, long? eventTime = null)
    {
        if (!down)
        {
            if (!pressed.Remove(identity, out var released)) return [];
            if (released is { } known && --keyCounts[known] == 0) keyCounts.Remove(known);
            foreach (var rule in rules)
            {
                if (pressed.Count == 0) { rule.Blocked = false; rule.Progress = 0; }
                else if (rule.Progress != 0) rule.Blocked = true;
            }
            return [];
        }
        if (!pressed.TryAdd(identity, key)) return [];
        if (key is { } mapped)
        {
            var count = keyCounts.GetValueOrDefault(mapped);
            keyCounts[mapped] = count + 1;
            if (count != 0) return [];
        }
        List<KeyboardTriggerBinding>? matches = null;
        foreach (var rule in rules)
        {
            if (rule.Blocked) continue;
            if (eventTime is { } timestamp && timestamp <= rule.ActivatedAt)
            {
                rule.Blocked = true;
                continue;
            }
            if (key is null || rule.Progress >= rule.Binding.Keys.Count || rule.Binding.Keys[rule.Progress] != key
                || keyCounts.Count != rule.Progress + 1 || pressed.Values.Any(value => value is null))
            {
                rule.Blocked = true;
                continue;
            }
            if (++rule.Progress == rule.Binding.Keys.Count)
            {
                rule.Blocked = true;
                (matches ??= []).Add(rule.Binding);
            }
        }
        return matches is null ? [] : matches;
    }

    private sealed class Rule(KeyboardTriggerBinding binding)
    {
        internal KeyboardTriggerBinding Binding { get; } = binding;
        internal int Progress;
        internal bool Blocked;
        internal long ActivatedAt;
    }
}
