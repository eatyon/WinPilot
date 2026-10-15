using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Features.Security;

namespace WinPilot.Test;

public sealed class LoginRateLimiterTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureCountExpiresAtFiveMinutesOfInactivity(bool expired)
    {
        var clock = new Clock();
        var limiter = Create(clock);
        for (var index = 0; index < 4; index++) Assert.False(limiter.Failure("source"));
        clock.Advance(TimeSpan.FromMinutes(5) - (expired ? TimeSpan.Zero : TimeSpan.FromTicks(1)));
        Assert.Equal(!expired, limiter.Failure("source"));
        Assert.Equal(!expired, limiter.IsLocked("source"));
        if (expired)
        {
            for (var index = 0; index < 3; index++) Assert.False(limiter.Failure("source"));
            Assert.True(limiter.Failure("source"));
        }
    }

    [Fact]
    public void EachCountedFailureRefreshesInactivityDeadlineButQueriesDoNot()
    {
        var clock = new Clock();
        var limiter = Create(clock);
        Assert.False(limiter.Failure("source"));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(limiter.Failure("source"));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(limiter.IsLocked("source"));
        Assert.Single(Attempts(limiter).Keys.Cast<object>());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(limiter.IsLocked("source"));
        Assert.Empty(Attempts(limiter).Keys.Cast<object>());
    }

    [Fact]
    public void ActiveLockIsRetainedWithoutExtendingAndExpiryRestartsAtOne()
    {
        var clock = new Clock();
        var limiter = Create(clock);
        for (var index = 0; index < 4; index++) Assert.False(limiter.Failure("source"));
        Assert.True(limiter.Failure("source"));
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        for (var index = 0; index < 20; index++)
        {
            Assert.True(limiter.IsLocked("source"));
            Assert.False(limiter.Failure("source"));
        }
        Assert.Single(Attempts(limiter).Keys.Cast<object>());
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(limiter.IsLocked("source"));
        Assert.False(limiter.Failure("source"));
        for (var index = 0; index < 3; index++) Assert.False(limiter.Failure("source"));
        Assert.True(limiter.Failure("source"));
    }

    [Fact]
    public void SuccessImmediatelyRemovesFailuresAndLocks()
    {
        var clock = new Clock();
        var limiter = Create(clock);
        Assert.False(limiter.Failure("partial"));
        for (var index = 0; index < 5; index++) limiter.Failure("locked");
        limiter.Success("partial");
        limiter.Success("locked");
        Assert.Empty(Attempts(limiter).Keys.Cast<object>());
        Assert.False(limiter.IsLocked("locked"));
        for (var index = 0; index < 4; index++) Assert.False(limiter.Failure("locked"));
        Assert.True(limiter.Failure("locked"));
    }

    [Fact]
    public void CleanupRunsOnAccessAtLowFrequencyAndNeverRemovesActiveLocks()
    {
        var clock = new Clock();
        var limiter = Create(clock);
        limiter.Failure("expired");
        clock.Advance(TimeSpan.FromMinutes(4));
        for (var index = 0; index < 5; index++) limiter.Failure("locked");
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, Attempts(limiter).Count);
        limiter.IsLocked("other");
        Assert.Single(Attempts(limiter).Keys.Cast<object>());
        Assert.True(limiter.IsLocked("locked"));
        Assert.False(limiter.Failure("new"));
        clock.Advance(TimeSpan.FromMinutes(5));
        limiter.Success("absent");
        Assert.Empty(Attempts(limiter).Keys.Cast<object>());
    }

    [Fact]
    public void ExpiredEntriesWaitForTheNextCleanupIntervalInsteadOfSweepingEveryAccess()
    {
        var clock = new Clock();
        var limiter = Create(clock);
        limiter.IsLocked("other");
        clock.Advance(TimeSpan.FromSeconds(10));
        limiter.Failure("source");
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        limiter.IsLocked("other");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(limiter.IsLocked("source"));
        Assert.Single(Attempts(limiter).Keys.Cast<object>());
        clock.Advance(TimeSpan.FromSeconds(59));
        limiter.IsLocked("other");
        Assert.Empty(Attempts(limiter).Keys.Cast<object>());
    }

    [Fact]
    public void StaleCleanupSnapshotCannotRemoveRecreatedRecordWithIdenticalValues()
    {
        var clock = new Clock();
        var limiter = Create(clock);
        limiter.Failure("source");
        var dictionary = Attempts(limiter);
        var original = ((IEnumerable)dictionary).Cast<object>().Single();
        limiter.Success("source");
        limiter.Failure("source");
        var collection = dictionary.GetType().GetInterfaces().Single(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICollection<>));
        Assert.False((bool)collection.GetMethod("Remove")!.Invoke(dictionary, [original])!);
        Assert.Single(dictionary.Keys.Cast<object>());
    }

    [Fact]
    public async Task ConcurrentCleanupAndFailureDoNotLoseTheFreshFailure()
    {
        var clock = new Clock();
        var limiter = Create(clock);
        for (var iteration = 0; iteration < 50; iteration++)
        {
            limiter.Success("source");
            for (var index = 0; index < 4; index++) Assert.False(limiter.Failure("source"));
            clock.Advance(TimeSpan.FromMinutes(5));
            using var start = new Barrier(2);
            var updating = Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); return limiter.Failure("source"); });
            var cleaning = Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); limiter.IsLocked("other"); });
            await Task.WhenAll(updating, cleaning).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await updating);
            Assert.Single(Attempts(limiter).Keys.Cast<object>());
            for (var index = 0; index < 3; index++) Assert.False(limiter.Failure("source"));
            Assert.True(limiter.Failure("source"));
        }
    }

    private static LoginRateLimiter Create(TimeProvider clock)
    {
        using var services = new ServiceCollection().AddSingleton(clock).BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<LoginRateLimiter>(services);
    }

    private static IDictionary Attempts(LoginRateLimiter limiter) =>
        (IDictionary)typeof(LoginRateLimiter).GetField("attempts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(limiter)!;

    private sealed class Clock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan interval) => Interlocked.Add(ref ticks, interval.Ticks);
    }
}
