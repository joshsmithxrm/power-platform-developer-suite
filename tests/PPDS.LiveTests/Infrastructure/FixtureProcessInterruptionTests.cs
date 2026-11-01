using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace PPDS.LiveTests.Infrastructure;

public sealed class FixtureProcessInterruptionTests
{
    [Fact]
    public async Task ConfirmedExitStillSuppressesCleanupAndPreservesPrimary()
    {
        var process = new ControlledProcess();
        process.OnKill = () => process.Exited = true;
        var interruption = new FixtureProcessInterruption();
        var primary = new TimeoutException("CLI command deadline");

        var actual = await Assert.ThrowsAsync<TimeoutException>(() =>
            interruption.ObserveAsync(process, () => ThrowFromCommand(primary)));

        actual.Should().BeSameAs(primary);
        actual.StackTrace.Should().Contain(nameof(ThrowFromCommand));
        process.KillCalls.Should().Be(1);
        process.WaitCalls.Should().Be(1);
        process.WaitToken.IsCancellationRequested.Should().BeFalse();
        interruption.Evidence!.ExitConfirmed.Should().BeTrue();
        interruption.Evidence.RemoteOutcome.Should().Be("unknown");
        var writes = 0;
        (await interruption.RunCleanupIfSafeAsync(() => { writes++; return Task.CompletedTask; })).Should().BeFalse();
        writes.Should().Be(0, "a stopped process does not prove that a submitted remote operation was rolled back");
    }

    [Fact]
    public async Task UnconfirmedExitIsBoundedAndSuppressesCleanup()
    {
        var clock = new ControlledTimeProvider();
        var neverExits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new ControlledProcess
        {
            OnKill = () => throw new System.ComponentModel.Win32Exception("Termination failed"),
            OnWait = _ => neverExits.Task // Model a wait that ignores cancellation too.
        };
        var interruption = new FixtureProcessInterruption(clock);
        var primary = new OperationCanceledException("Original command cancellation");
        var pending = interruption.ObserveAsync(process, () => ThrowFromCommand(primary));
        pending.IsCompleted.Should().BeFalse();
        clock.DueTime.Should().Be(TimeSpan.FromSeconds(30));
        clock.ExpireDeadline();

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        actual.Should().BeSameAs(primary);
        interruption.RequiresManualReview.Should().BeTrue();
        interruption.Evidence!.ExitConfirmed.Should().BeFalse();
        interruption.Evidence.Errors.Should().Contain("terminate: Win32Exception");
        interruption.Evidence.Errors.Should().Contain(error => error.StartsWith("confirm-exit: ", StringComparison.Ordinal));
        (await interruption.RunCleanupIfSafeAsync(() => throw new InvalidOperationException("Must not write"))).Should().BeFalse();
        neverExits.SetResult();
    }

    [Fact]
    public async Task ExitDuringKillPreservesPrimaryAndConfirmsExit()
    {
        using var commandCancellation = new CancellationTokenSource();
        commandCancellation.Cancel();
        var primary = new OperationCanceledException(commandCancellation.Token);
        var process = new ControlledProcess();
        process.OnKill = () => { process.Exited = true; throw new InvalidOperationException("Already exited"); };
        var interruption = new FixtureProcessInterruption();

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            interruption.ObserveAsync(process, () => ThrowFromCommand(primary)));

        actual.Should().BeSameAs(primary);
        process.WaitCalls.Should().Be(1);
        process.WaitToken.Should().NotBe(commandCancellation.Token);
        process.WaitToken.IsCancellationRequested.Should().BeFalse();
        interruption.Evidence!.ExitConfirmed.Should().BeTrue();
        interruption.Evidence.Errors.Should().BeEmpty();
        (await interruption.RunCleanupIfSafeAsync(() => throw new InvalidOperationException("Must not write"))).Should().BeFalse();
    }

    [Fact]
    public async Task AlreadyExitedProcessWithInvalidResponseStillSuppressesCleanup()
    {
        var process = new ControlledProcess { Exited = true };
        var interruption = new FixtureProcessInterruption();
        var primary = new System.Text.Json.JsonException("Incomplete candidate response");
        var actual = await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            interruption.ObserveAsync(process, () => ThrowFromCommand(primary)));
        actual.Should().BeSameAs(primary);
        process.KillCalls.Should().Be(0);
        process.WaitCalls.Should().Be(1);
        interruption.Evidence!.ExitConfirmed.Should().BeTrue();
        interruption.Evidence.RemoteOutcome.Should().Be("unknown");
        (await interruption.RunCleanupIfSafeAsync(() => throw new InvalidOperationException("Must not write"))).Should().BeFalse();
    }

    [Fact]
    public async Task NativeChildProcessIsStoppedAndAwaited()
    {
        // A harmless local child waits on its own redirected stdin. Static arguments, shell=false;
        // no CLI, credentials, network, or Dataverse operation is involved.
        var start = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/cat")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("more");
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start local child");
        try
        {
            var interruption = new FixtureProcessInterruption();
            var primary = new TimeoutException("Controlled native-child interruption");
            var actual = await Assert.ThrowsAsync<TimeoutException>(() =>
                interruption.ObserveAsync(process, () => ThrowFromCommand(primary)));
            actual.Should().BeSameAs(primary);
            process.HasExited.Should().BeTrue();
            interruption.Evidence!.ExitConfirmed.Should().BeTrue();
            interruption.RequiresManualReview.Should().BeTrue();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task UninterruptedFixtureAllowsCleanup()
    {
        var process = new ControlledProcess();
        var interruption = new FixtureProcessInterruption();
        (await interruption.ObserveAsync(process, () => Task.FromResult(42))).Should().Be(42);
        interruption.RequiresManualReview.Should().BeFalse();
        interruption.Evidence.Should().BeNull();
        process.KillCalls.Should().Be(0);
        process.WaitCalls.Should().Be(0);
        var calls = 0;
        (await interruption.RunCleanupIfSafeAsync(() => { calls++; return Task.CompletedTask; })).Should().BeTrue();
        calls.Should().Be(1);
    }

    private static Task<int> ThrowFromCommand(Exception exception) => throw exception;

    private sealed class ControlledProcess : IFixtureProcess
    {
        public bool Exited { get; set; }
        public bool HasExited => Exited;
        public int KillCalls { get; private set; }
        public int WaitCalls { get; private set; }
        public CancellationToken WaitToken { get; private set; }
        public Action? OnKill { get; set; }
        public Func<CancellationToken, Task>? OnWait { get; set; }
        public void Kill() { KillCalls++; OnKill?.Invoke(); }
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCalls++; WaitToken = cancellationToken;
            return OnWait?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private ControlledTimer? _timer;
        public TimeSpan DueTime { get; private set; }
        public void ExpireDeadline() => _timer!.Fire();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return _timer = new ControlledTimer(callback, state);
        }
        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
