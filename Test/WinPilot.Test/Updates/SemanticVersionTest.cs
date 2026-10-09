using WinPilot.Features.Updates;

namespace WinPilot.Test;

public sealed class SemanticVersionTest
{
    [Theory]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-beta.10", "1.0.0-rc.1", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0+old", "v1.0.0+new", 0)]
    [InlineData("1.0.0-beta+old", "1.0.0-beta+new", 0)]
    [InlineData("2.0.0-beta", "1.99.99", 1)]
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-beta.999999999999999999999", "1.0.0-beta.1000000000000000000000", -1)]
    public void VersionsFollowSemVerPrecedence(string left, string right, int sign)
    {
        Assert.True(SemanticVersion.TryParse(left, out var first));
        Assert.True(SemanticVersion.TryParse(right, out var second));
        Assert.Equal(sign, Math.Sign(first.CompareTo(second)));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-beta.02")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+bad.")]
    [InlineData("1.0.0\n")]
    public void InvalidVersionsAreRejected(string value)
        => Assert.False(SemanticVersion.TryParse(value, out _));
}
