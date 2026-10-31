using Xunit;

namespace PPDS.LiveTests.Infrastructure;

public class PropagationWaitTests
{
    [Theory]
    [InlineData(null, 90)]
    [InlineData("", 90)]
    [InlineData("1", 1)]
    [InlineData("180", 180)]
    public void TimeoutConfiguration_IsBounded(string? value, int expected)
        => Assert.Equal(TimeSpan.FromSeconds(expected), PropagationWait.TimeoutFromEnvironment(value));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("901")]
    [InlineData("no")]
    public void TimeoutConfiguration_RejectsInvalidValues(string value)
        => Assert.Throws<ArgumentException>(() => PropagationWait.TimeoutFromEnvironment(value));

    [Fact]
    public async Task Poll_SucceedsAfterPropagationWithoutWallClockDelay()
    {
        var clock = new ManualClock();
        var attempts = 0;
        var task = PropagationWait.UntilAsync(
            _ => Task.FromResult(++attempts == 2 ? "ready" : "drift"),
            value => value == "ready", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), clock: clock);
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        Assert.Equal("ready", result.Last);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Elapsed);
    }

    [Fact]
    public async Task Poll_DeadlineRetainsLastResultAndAttempts()
    {
        var clock = new ManualClock();
        var task = PropagationWait.UntilAsync(_ => Task.FromResult("exit=1; drift=missing step"),
            _ => false, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), clock: clock);
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal("exit=1; drift=missing step", result.Last);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Elapsed);
    }

    [Fact]
    public async Task Poll_DeadlineCancelsInFlightCommand()
    {
        var clock = new ManualClock();
        var task = PropagationWait.UntilAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }, _ => true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), clock: clock);
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Succeeded);
        Assert.Null(result.Last);
        Assert.Equal(1, result.Attempts);
        Assert.Contains("deadline", result.LastError);
    }

    [Fact]
    public async Task Poll_CallerCancellationRemainsCancellation()
    {
        var clock = new ManualClock();
        using var cts = new CancellationTokenSource();
        var task = PropagationWait.UntilAsync(_ => Task.FromResult("drift"), _ => false,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), cts.Token, clock);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Poll_CommandTimeoutRetainsDiagnostics()
    {
        var result = await PropagationWait.UntilAsync<string>(
            _ => throw new TimeoutException("diff process timed out; last stderr: unavailable"),
            _ => false, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), clock: new ManualClock());
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Contains("diff process timed out", result.LastError);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _ticks += duration.Ticks;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long _due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                return !_disposed;
            }
            public void FireIfDue()
            {
                if (_disposed || _due > owner._ticks) return;
                _due = long.MaxValue;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
