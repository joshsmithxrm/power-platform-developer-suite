using System.Diagnostics;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Stops an interrupted fixture process without treating its exit as a remote rollback.</summary>
internal sealed class FixtureProcessInterruption(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool RequiresManualReview { get; private set; }
    public FixtureProcessStopEvidence? Evidence { get; private set; }

    public Task<T> ObserveAsync<T>(Process process, Func<Task<T>> operation) =>
        ObserveAsync(new NativeFixtureProcess(process), operation);

    internal async Task<T> ObserveAsync<T>(IFixtureProcess process, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch
        {
            // Set the cleanup prohibition before attempting any termination work. Even an
            // exited client can have left an in-flight Dataverse request with an unknown outcome.
            RequiresManualReview = true;
            await StopAsync(process);
            throw;
        }
    }

    public async Task<bool> RunCleanupIfSafeAsync(Func<Task> cleanup)
    {
        if (RequiresManualReview) return false;
        await cleanup();
        return true;
    }

    private async Task StopAsync(IFixtureProcess process)
    {
        var errors = new List<string>();
        var exitConfirmed = false;
        try
        {
            // This token deliberately does not link to the already-cancelled command/run token.
            using var deadline = new CancellationTokenSource(StopTimeout, _timeProvider);
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between the check and Kill. Still await exit below.
                }
                catch (Exception exception)
                {
                    errors.Add("terminate: " + exception.GetType().Name);
                }
            }
            await process.WaitForExitAsync(deadline.Token).WaitAsync(deadline.Token);
            exitConfirmed = process.HasExited;
        }
        catch (Exception exception)
        {
            // Stopping must never replace the primary failure. Unconfirmed exit forbids cleanup.
            errors.Add("confirm-exit: " + exception.GetType().Name);
        }
        Evidence = new(exitConfirmed, "unknown", errors.ToArray());
    }

    private sealed class NativeFixtureProcess(Process process) : IFixtureProcess
    {
        public bool HasExited => process.HasExited;
        public void Kill() => process.Kill(entireProcessTree: true);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    }
}

internal interface IFixtureProcess
{
    bool HasExited { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed record FixtureProcessStopEvidence(bool ExitConfirmed, string RemoteOutcome, IReadOnlyList<string> Errors);
