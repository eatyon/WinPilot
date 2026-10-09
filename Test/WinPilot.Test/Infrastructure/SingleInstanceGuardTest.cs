using WinPilot.Infrastructure;

namespace WinPilot.Test;

public sealed class SingleInstanceGuardTest
{
    [Fact]
    public void TryAcquire_AllowsOnlyOneInstance()
    {
        var name = @"Local\WinPilot.Test." + Guid.NewGuid().ToString("N");
        using var firstInstance = SingleInstanceGuard.TryAcquire(name, name + ".ShuttingDown");

        Assert.NotNull(firstInstance);
        Assert.Null(SingleInstanceGuard.TryAcquire(name, name + ".ShuttingDown"));
    }

    [Fact]
    public void TryAcquire_DifferentScopesDoNotBlockEachOther()
    {
        var prefix = @"Local\WinPilot.Test." + Guid.NewGuid().ToString("N");
        var firstName = prefix + ".User1";
        var secondName = prefix + ".User2";
        using var firstInstance = SingleInstanceGuard.TryAcquire(firstName, firstName + ".ShuttingDown");
        using var secondInstance = SingleInstanceGuard.TryAcquire(secondName, secondName + ".ShuttingDown");

        Assert.NotNull(firstInstance);
        Assert.NotNull(secondInstance);
        Assert.Null(SingleInstanceGuard.TryAcquire(firstName, firstName + ".ShuttingDown"));
        Assert.Null(SingleInstanceGuard.TryAcquire(secondName, secondName + ".ShuttingDown"));
    }
}
