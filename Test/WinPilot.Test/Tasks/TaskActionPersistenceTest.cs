using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;

namespace WinPilot.Test;

public sealed class TaskActionPersistenceTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentUserOnlyRoundTripsForEndProcess(bool currentUserOnly)
    {
        var stored = TaskActionPersistence.From(new TaskAction(
            TaskActionType.EndProcess, ProcessName: "app.exe", CurrentUserOnly: currentUserOnly));

        var restored = stored.ToAction();
        Assert.Equal("EndProcess", stored.Type);
        Assert.Equal(currentUserOnly, restored.CurrentUserOnly);
        Assert.Equal("app.exe", restored.ProcessName);
        Assert.Equal(currentUserOnly ? true : (bool?)null, stored.CurrentUserOnly);
    }

    [Fact]
    public void MissingCurrentUserOnlyDefaultsToDisabled()
    {
        Assert.False(new TaskActionPersistence { Type = "EndProcess", ProcessName = "app.exe" }.ToAction().CurrentUserOnly);
    }

    [Fact]
    public void ContinueOnFailureRoundTripsWhenEnabled()
    {
        var stored = TaskActionPersistence.From(new TaskAction(
            TaskActionType.Delay,
            DelaySeconds: 1,
            ContinueOnFailure: true));

        Assert.True(stored.ContinueOnFailure);
        Assert.True(stored.ToAction().ContinueOnFailure);
    }

    [Fact]
    public void MissingContinueOnFailureDefaultsToDisabled()
    {
        var action = new TaskActionPersistence
        {
            Type = TaskActionType.Delay.ToString(),
            DelaySeconds = 1
        }.ToAction();

        Assert.False(action.ContinueOnFailure);
    }
}
