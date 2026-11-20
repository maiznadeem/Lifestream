using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>
/// The manual clock tells a test how many <c>Task.Delay</c> calls are waiting on it, and tests step the clock only when the code they watch has
/// reached one. That rests on one fact about .NET: <c>Task.Delay(time, clock)</c> gives the clock's timer the task it will complete as its
/// state, and no other user of a timer here does. If a later .NET did it another way, the tests that wait for a delay would time out; this
/// says why first.
/// </summary>
public sealed class ManualTimeProviderTests
{
    [Fact]
    public async Task ADelayOnTheClock_IsCounted_AndOtherTimersAreNot()
    {
        var clock = new ManualTimeProvider();
        Assert.Equal(0, clock.DelaysWaiting);

        await using var timer = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120), clock);
        Assert.Equal(0, clock.DelaysWaiting); // a timer of the code's own, and a time limit: not a delay

        var first = Task.Delay(TimeSpan.FromSeconds(2), clock, TestContext.Current.CancellationToken);
        var second = Task.Delay(TimeSpan.FromSeconds(4), clock, TestContext.Current.CancellationToken);
        Assert.Equal(2, clock.DelaysWaiting);

        clock.Advance(TimeSpan.FromSeconds(2));
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, clock.DelaysWaiting);

        clock.Advance(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, clock.DelaysWaiting);
        Assert.Equal(2, clock.TimersFired);
    }

    [Fact]
    public async Task ACancelledDelay_IsNoLongerWaiting()
    {
        var clock = new ManualTimeProvider();
        using var cancel = new CancellationTokenSource();
        var delay = Task.Delay(TimeSpan.FromSeconds(2), clock, cancel.Token);
        Assert.Equal(1, clock.DelaysWaiting);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay);
        Assert.Equal(0, clock.DelaysWaiting);
    }
}
