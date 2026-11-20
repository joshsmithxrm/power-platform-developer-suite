using Xunit;

namespace PPDS.Tests.Shared;

/// <summary>
/// The upper bound on a test's wait for a completion signal (an RPC response, a reported error, a child
/// process exiting).
/// </summary>
/// <remarks>
/// One source file, linked into each test project that needs it (the repository's pattern for shared test
/// code, as with FixtureProcessInterruption.cs), so every project gets the same bound and message without
/// a shared test assembly or InternalsVisibleTo. Python tests use the same rule with their own constant.
/// <para/>
/// It is a hang guard, not a speed assertion: a test that waits for its signal finishes as soon as the
/// signal arrives, so the bound never decides the outcome of a correct test. It is deliberately not
/// derived from observed durations: the in-process work these tests wait for takes milliseconds idle,
/// but on a loaded CI runner (every test assembly for three frameworks running at once, with coverage)
/// a short fixed deadline fired with the behaviour still correct (#1684, #1795). Two minutes still
/// ends a genuinely wedged test long before the job's own timeout, and the failure names what was awaited.
/// The wait is raced against the bound, so a hang that ignores the cancellation token, or blocks the
/// thread it runs on, fails at the bound as well.
/// </remarks>
internal static class HangBackstop
{
    /// <summary>The hang-guard bound.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Replaces <see cref="Bound"/> for the two-argument overloads within the current async flow only, so the
    /// tests of this helper can show those overloads apply a bound without waiting two minutes. Nothing else sets it.
    /// </summary>
    internal static readonly AsyncLocal<TimeSpan?> BoundOverrideForHelperTests = new();

    private static TimeSpan DefaultBound => BoundOverrideForHelperTests.Value ?? Bound;

    /// <summary>
    /// Runs <paramref name="operation"/> and waits for it under <see cref="Bound"/>. The token it is given
    /// is cancelled when the bound elapses. Whether or not the operation observes that token, the test
    /// fails at the bound with a message naming <paramref name="awaiting"/>, never with a bare
    /// <see cref="OperationCanceledException"/>. An exception the operation throws before then surfaces unchanged.
    /// </summary>
    public static Task<T> WaitAsync<T>(Func<CancellationToken, Task<T>> operation, string awaiting)
        => WaitAsync(operation, awaiting, DefaultBound);

    /// <summary>
    /// Waits for <paramref name="signal"/> to complete under <see cref="Bound"/>, failing the test as described in
    /// <see cref="WaitAsync{T}(Func{CancellationToken, Task{T}}, string)"/> if the bound elapses first.
    /// </summary>
    public static Task<T> WaitAsync<T>(Task<T> signal, string awaiting)
        => WaitAsync(_ => signal, awaiting, DefaultBound);

    /// <summary>As <see cref="WaitAsync{T}(Func{CancellationToken, Task{T}}, string)"/> for an operation with no result.</summary>
    public static Task WaitAsync(Func<CancellationToken, Task> operation, string awaiting)
        => WaitAsync(operation, awaiting, DefaultBound);

    /// <summary>As <see cref="WaitAsync{T}(Task{T}, string)"/> for a signal with no result.</summary>
    public static Task WaitAsync(Task signal, string awaiting)
        => WaitAsync(_ => signal, awaiting, DefaultBound);

    /// <summary>As <see cref="WaitAsync(Func{CancellationToken, Task}, string)"/> with an explicit bound (tests of this helper only).</summary>
    internal static Task WaitAsync(Func<CancellationToken, Task> operation, string awaiting, TimeSpan bound)
        => WaitAsync<bool>(async token => { await operation(token); return true; }, awaiting, bound);

    /// <summary>
    /// As <see cref="WaitAsync{T}(Func{CancellationToken, Task{T}}, string)"/> with an explicit bound.
    /// Only the tests of this helper pass one, to exercise the firing path quickly.
    /// </summary>
    internal static async Task<T> WaitAsync<T>(Func<CancellationToken, Task<T>> operation, string awaiting, TimeSpan bound)
    {
        using var backstop = new CancellationTokenSource();
        using var timer = new CancellationTokenSource();
        // Task.Run: an operation that blocks before its first await must not block the race itself.
        var work = Task.Run(() => operation(backstop.Token));
        var deadline = Task.Delay(bound, timer.Token);

        if (await Task.WhenAny(work, deadline) != work)
        {
            backstop.Cancel();
            // The operation may still fault later; observe it so it does not surface as an unobserved exception.
            _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Assert.Fail($"No completion after {Describe(bound)} waiting for {awaiting}. " +
                "This bound only catches a hang; treat the failure as one, not as slowness.");
        }

        timer.Cancel();
        return await work;
    }

    private static string Describe(TimeSpan bound)
        => bound >= TimeSpan.FromSeconds(1) ? $"{bound.TotalSeconds:0}s" : $"{bound.TotalMilliseconds:0}ms";
}
