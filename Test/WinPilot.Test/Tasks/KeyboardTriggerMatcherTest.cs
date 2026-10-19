using WinPilot.Core.Input;
using WinPilot.Features.Tasks;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class KeyboardTriggerMatcherTest
{
    private static KeyboardInputIdentity Id(KeyboardKey key) => KeyboardInputIdentity.FromScanCode((int)key + 1, 0, false);
    private static IReadOnlyList<KeyboardTriggerBinding> Input(KeyboardTriggerMatcher matcher, KeyboardKey key, bool down = true) => matcher.Input(Id(key), key, down);

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ConfiguredOrderTriggersOnceUntilEverythingIsReleased(int count)
    {
        var keys = new[] { KeyboardKey.Ctrl, KeyboardKey.A, KeyboardKey.B }.Take(count).ToArray();
        var binding = new KeyboardTriggerBinding(new(), keys);
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([binding]);
        for (int i = 0; i < count; i++) Assert.Equal(i == count - 1 ? 1 : 0, Input(matcher, keys[i]).Count);
        foreach (var key in keys) Assert.Empty(Input(matcher, key));
        Assert.Empty(Input(matcher, keys[^1], false));
        if (count > 1) Assert.Empty(Input(matcher, keys[^1]));
        foreach (var key in keys) Input(matcher, key, false);
        for (int i = 0; i < count; i++) Assert.Equal(i == count - 1 ? 1 : 0, Input(matcher, keys[i]).Count);
    }

    [Theory]
    [InlineData("order")] [InlineData("release")] [InlineData("extra")] [InlineData("unknown")]
    public void InvalidRoundRequiresFullRelease(string reason)
    {
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([new(new(), [KeyboardKey.A, KeyboardKey.B])]);
        if (reason == "order") { Assert.Empty(Input(matcher, KeyboardKey.B)); Assert.Empty(Input(matcher, KeyboardKey.A)); }
        else
        {
            Input(matcher, KeyboardKey.A);
            if (reason == "release") Input(matcher, KeyboardKey.A, false);
            if (reason == "extra") Input(matcher, KeyboardKey.C);
            if (reason == "unknown") matcher.Input(new(999, 999, false), null, true);
            Assert.Empty(Input(matcher, KeyboardKey.B));
        }
        foreach (var key in new[] { KeyboardKey.A, KeyboardKey.B, KeyboardKey.C }) Input(matcher, key, false);
        matcher.Input(new(999, 999, false), null, false);
        Assert.Empty(Input(matcher, KeyboardKey.A));
        Assert.Single(Input(matcher, KeyboardKey.B));
    }

    [Fact]
    public void ReleasingAnExtraKeyDoesNotTriggerAndRulesDoNotBlockEachOther()
    {
        var matcher = new KeyboardTriggerMatcher();
        var ab = new KeyboardTriggerBinding(new(), [KeyboardKey.A, KeyboardKey.B]);
        var ba = new KeyboardTriggerBinding(new(), [KeyboardKey.B, KeyboardKey.A]);
        matcher.Update([ab, ba]);
        Input(matcher, KeyboardKey.C);
        Input(matcher, KeyboardKey.A);
        Assert.Empty(Input(matcher, KeyboardKey.B));
        Assert.Empty(Input(matcher, KeyboardKey.C, false));
        Input(matcher, KeyboardKey.A, false); Input(matcher, KeyboardKey.B, false);
        Input(matcher, KeyboardKey.B);
        Assert.Same(ba, Assert.Single(Input(matcher, KeyboardKey.A)));
    }

    [Fact]
    public void PrefixAndLongerRulesEachTriggerWhenTheirOwnSequenceCompletes()
    {
        var single = new KeyboardTriggerBinding(new(), [KeyboardKey.A]);
        var combination = new KeyboardTriggerBinding(new(), [KeyboardKey.A, KeyboardKey.B]);
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([single, combination]);
        Assert.Same(single, Assert.Single(Input(matcher, KeyboardKey.A)));
        Assert.Same(combination, Assert.Single(Input(matcher, KeyboardKey.B)));
        Assert.Empty(Input(matcher, KeyboardKey.A));
        Assert.Empty(Input(matcher, KeyboardKey.B));
        Input(matcher, KeyboardKey.B, false);
        Input(matcher, KeyboardKey.A, false);
        Assert.Same(single, Assert.Single(Input(matcher, KeyboardKey.A)));
        Assert.Same(combination, Assert.Single(Input(matcher, KeyboardKey.B)));
    }

    [Fact]
    public void ReplacingRulesWhileKeysAreHeldDoesNotCompleteAnOldOrNewRule()
    {
        var matcher = new KeyboardTriggerMatcher();
        var old = new KeyboardTriggerBinding(new(), [KeyboardKey.A, KeyboardKey.B]);
        var next = new KeyboardTriggerBinding(new(), [KeyboardKey.A, KeyboardKey.B]);
        matcher.Update([old]); Input(matcher, KeyboardKey.A);
        matcher.Update([next]);
        Assert.Empty(Input(matcher, KeyboardKey.B));
        Input(matcher, KeyboardKey.A, false); Input(matcher, KeyboardKey.B, false);
        Input(matcher, KeyboardKey.A);
        Assert.Same(next, Assert.Single(Input(matcher, KeyboardKey.B)));
        matcher.Update([]);
        Assert.Empty(Input(matcher, KeyboardKey.C));
    }

    [Fact]
    public void ResetDiscardsPreviouslyObservedKeysAndPartialProgress()
    {
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([new(new(), [KeyboardKey.Ctrl, KeyboardKey.A])]);
        Input(matcher, KeyboardKey.Ctrl);
        matcher.Reset();
        Assert.Empty(Input(matcher, KeyboardKey.Ctrl, false));
        Assert.Empty(Input(matcher, KeyboardKey.A));
        Input(matcher, KeyboardKey.A, false);
        Input(matcher, KeyboardKey.Ctrl);
        Assert.Single(Input(matcher, KeyboardKey.A));
    }

    [Theory]
    [InlineData(0x0d, false, KeyboardKey.Enter)]
    [InlineData(0x0d, true, KeyboardKey.NumpadEnter)]
    [InlineData(0xa2, false, KeyboardKey.Ctrl)]
    [InlineData(0xa3, true, KeyboardKey.Ctrl)]
    [InlineData(0x5c, true, KeyboardKey.Win)]
    [InlineData(0x13, false, KeyboardKey.Pause)]
    [InlineData(0x2c, true, KeyboardKey.PrintScreen)]
    public void NativeKeyMappingMatchesTheExistingSelector(int key, bool extended, KeyboardKey expected) =>
        Assert.Equal(expected, WindowsKeyboardEventListener.MapKey(key, 0, extended));

    [Theory]
    [InlineData(0x24, 0x47, false, KeyboardKey.Numpad7)]
    [InlineData(0x24, 0x47, true, KeyboardKey.Home)]
    [InlineData(0x23, 0x4f, false, KeyboardKey.Numpad1)]
    [InlineData(0x23, 0x4f, true, KeyboardKey.End)]
    [InlineData(0x2e, 0x53, false, KeyboardKey.NumpadDecimal)]
    [InlineData(0x2e, 0x53, true, KeyboardKey.Delete)]
    [InlineData(0x0c, 0x4c, false, KeyboardKey.Numpad5)]
    public void NumpadKeysRetainTheirIdentityWhenNumLockIsOff(int key, int scan, bool extended, KeyboardKey expected) =>
        Assert.Equal(expected, WindowsKeyboardEventListener.MapKey(key, scan, extended));

    [Fact]
    public void QueuedInputFromBeforeRegistrationCannotTriggerNewRules()
    {
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([new(new(), [KeyboardKey.A])], activatedAt: 100);
        Assert.Empty(matcher.Input(Id(KeyboardKey.A), KeyboardKey.A, true, 99));
        Input(matcher, KeyboardKey.A, false);
        Assert.Single(matcher.Input(Id(KeyboardKey.A), KeyboardKey.A, true, 101));
    }

    [Fact]
    public void BothPhysicalModifierKeysMustBeReleasedBeforeReset()
    {
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([new(new(), [KeyboardKey.Ctrl, KeyboardKey.A])]);
        Input(matcher, KeyboardKey.Ctrl);
        var right = new KeyboardInputIdentity(999, 999, true);
        Assert.Empty(matcher.Input(right, KeyboardKey.Ctrl, true));
        Assert.Single(Input(matcher, KeyboardKey.A));
        Input(matcher, KeyboardKey.A, false); Input(matcher, KeyboardKey.Ctrl, false);
        Assert.Empty(Input(matcher, KeyboardKey.A));
        Input(matcher, KeyboardKey.A, false); matcher.Input(right, KeyboardKey.Ctrl, false);
        Input(matcher, KeyboardKey.Ctrl);
        Assert.Single(Input(matcher, KeyboardKey.A));
    }

    [Fact]
    public void PhysicalReleaseStillMatchesIfVirtualKeyChangesWithNumLockOrShift()
    {
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([new(new(), [KeyboardKey.Numpad1])]);
        var down = KeyboardInputIdentity.FromScanCode(0x4f, 0x61, false);
        var up = KeyboardInputIdentity.FromScanCode(0x4f, 0x23, false);
        Assert.Equal(down, up);
        Assert.Single(matcher.Input(down, KeyboardKey.Numpad1, true));
        Assert.Empty(matcher.Input(up, KeyboardKey.End, false));
        Assert.Single(matcher.Input(down, KeyboardKey.Numpad1, true));
    }

    [Theory]
    [InlineData(0x4f, 0x61, 0x23)]
    [InlineData(0x4f, 0x23, 0x61)]
    [InlineData(0x4c, 0x65, 0x0c)]
    [InlineData(0x4c, 0x0c, 0x65)]
    [InlineData(0x53, 0x6e, 0x2e)]
    [InlineData(0x53, 0x2e, 0x6e)]
    public void NumpadInputObservedAfterResetReleasesDespiteVirtualKeyChange(int scan, int downVk, int upVk)
    {
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([new(new(), [KeyboardKey.A])]);
        matcher.Reset(100);
        var down = KeyboardInputIdentity.FromScanCode(scan, downVk, false);
        var up = KeyboardInputIdentity.FromScanCode(scan, upVk, false);
        var downKey = WindowsKeyboardEventListener.MapKey(downVk, scan, false);
        var upKey = WindowsKeyboardEventListener.MapKey(upVk, scan, false);
        Assert.Equal(down, up);
        Assert.Empty(matcher.Input(down, downKey, true, 101));
        Assert.Empty(matcher.Input(down, downKey, true, 102));
        Assert.Empty(matcher.Input(up, upKey, false, 103));
        Assert.Single(Input(matcher, KeyboardKey.A));
        Input(matcher, KeyboardKey.A, false);
        Assert.Single(Input(matcher, KeyboardKey.A));
    }

    [Fact]
    public void UnobservedReleaseAfterResetDoesNotBlockNewInput()
    {
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([new(new(), [KeyboardKey.A])]);
        matcher.Reset(100);
        Assert.Empty(matcher.Input(KeyboardInputIdentity.FromScanCode(0x4f, 0x23, false), KeyboardKey.Numpad1, false, 101));
        Assert.Single(matcher.Input(Id(KeyboardKey.A), KeyboardKey.A, true, 102));
    }

    [Fact]
    public void FirstDownObservedAfterResetCanMatchButFurtherRepeatsCannot()
    {
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([new(new(), [KeyboardKey.Numpad1])]);
        matcher.Reset(100);
        var down = KeyboardInputIdentity.FromScanCode(0x4f, 0x61, false);
        Assert.Single(matcher.Input(down, KeyboardKey.Numpad1, true, 101));
        Assert.Empty(matcher.Input(down, KeyboardKey.Numpad1, true, 102));
        Assert.Empty(matcher.Input(KeyboardInputIdentity.FromScanCode(0x4f, 0x23, false), KeyboardKey.Numpad1, false, 103));
        Assert.Single(matcher.Input(down, KeyboardKey.Numpad1, true, 104));
    }

    [Fact]
    public void LongRunningRulesAndResetBoundariesUseMonotonic64BitTime()
    {
        var matcher = new KeyboardTriggerMatcher();
        matcher.Update([new(new(), [KeyboardKey.A])], 100);
        long afterFiftyDays = (long)TimeSpan.FromDays(50).TotalMilliseconds;
        Assert.Single(matcher.Input(Id(KeyboardKey.A), KeyboardKey.A, true, afterFiftyDays));
        matcher.Reset(afterFiftyDays + 100);
        Assert.Empty(matcher.Input(Id(KeyboardKey.A), KeyboardKey.A, true, afterFiftyDays + 50));
        Input(matcher, KeyboardKey.A, false);
        Assert.Single(matcher.Input(Id(KeyboardKey.A), KeyboardKey.A, true, afterFiftyDays + 101));
    }
}
