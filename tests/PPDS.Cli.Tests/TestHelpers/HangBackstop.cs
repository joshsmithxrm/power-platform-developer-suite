using Xunit;

namespace PPDS.Cli.Tests.TestHelpers;

/// <summary>
/// The upper bound on a test's wait for a completion signal (an RPC response, a reported error).
/// </summary>
/// <remarks>
/// It is a hang guard, not a speed assertion: a test that waits for its signal finishes as soon as the
/// signal arrives, so the bound never decides the outcome of a correct test. It is deliberately not
/// derived from observed durations: the in-process work these tests wait for takes milliseconds idle,
/// but on a loaded CI runner (every test assembly for three frameworks running at once, with coverage)
/// a short fixed deadline fired with the behaviour still correct (#1684, #1795). Two minutes still
/// ends a genuinely wedged test long before the job's own timeout, and the failure names what was awaited.
/// </remarks>
internal static class HangBackstop
{
    /// <summary>The hang-guard bound.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs <paramref name="operation"/>, which must observe the token it is given. If the bound elapses
    /// first, the test fails with a message naming <paramref name="awaiting"/> instead of surfacing a bare
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<T> WaitAsync<T>(Func<CancellationToken, Task<T>> operation, string awaiting)
    {
        using var backstop = new CancellationTokenSource(Bound);
        try
        {
            return await operation(backstop.Token);
        }
        catch (OperationCanceledException) when (backstop.IsCancellationRequested)
        {
            Assert.Fail($"No completion after {Bound.TotalSeconds:0}s waiting for {awaiting}. " +
                "This bound only catches a hang; treat the failure as one, not as slowness.");
            throw; // Unreachable: Assert.Fail throws.
        }
    }

    /// <summary>
    /// Waits for <paramref name="signal"/> to complete, failing the test as described in
    /// <see cref="WaitAsync{T}(Func{CancellationToken, Task{T}}, string)"/> if the bound elapses first.
    /// </summary>
    public static Task<T> WaitAsync<T>(Task<T> signal, string awaiting)
        => WaitAsync(token => signal.WaitAsync(token), awaiting);
}
