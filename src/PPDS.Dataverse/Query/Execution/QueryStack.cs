using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Query.Execution;

/// <summary>
/// Stack protection for SQL processing (#1952). A stack overflow cannot be caught in .NET and ends the
/// process, so deep SQL must never reach one:
/// <list type="bullet">
/// <item><description>
/// Every recursive walk of ours calls <see cref="EnsureSufficientStack"/> on entry, which refuses with
/// <see cref="QueryNestedTooDeeplyException"/> while there is still stack left.
/// </description></item>
/// <item><description>
/// Work that recurses in code we cannot change (the ScriptDom lexer, parser and script generator) runs on a
/// dedicated thread with a large stack (<see cref="Run{T}(Func{T})"/>, <see cref="RunAsync{T}"/>), behind a
/// bound on the text's size that the caller checks against <see cref="CurrentThreadStackSize"/>.
/// </description></item>
/// </list>
/// Only synchronous code runs on the dedicated thread: it never awaits, and it never blocks on a task.
/// </summary>
public static class QueryStack
{
    /// <summary>
    /// The stack of the dedicated thread: 1 GiB. This is a reservation of address space; memory is committed
    /// only as the stack is used, and released when the thread exits.
    /// </summary>
    public const int DefaultStackSize = 1 << 30;

    /// <summary>The smallest stack <see cref="Run{T}(Func{T}, int)"/> falls back to when a larger one cannot be reserved.</summary>
    public const int MinimumFallbackStackSize = 64 << 20;

    [ThreadStatic]
    private static int t_stackSize;

    /// <summary>
    /// The stack size of the current thread when it is a dedicated thread started by this class; otherwise 0.
    /// </summary>
    public static int CurrentThreadStackSize => t_stackSize;

    /// <summary>
    /// Refuses with <see cref="QueryNestedTooDeeplyException"/> when the current thread has too little stack
    /// left to run an average .NET method (the runtime keeps about 128 KB in hand). Costs about a nanosecond.
    /// </summary>
    /// <exception cref="QueryNestedTooDeeplyException">The stack is nearly exhausted.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnsureSufficientStack()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            ThrowNestedTooDeeply();
    }

    /// <summary>
    /// True when <paramref name="exception"/> is a refusal for depth: a
    /// <see cref="QueryNestedTooDeeplyException"/>, or the runtime's own <see cref="InsufficientExecutionStackException"/>.
    /// </summary>
    /// <param name="exception">The exception to test.</param>
    /// <returns>True for a depth refusal.</returns>
    public static bool IsDepthRefusal(Exception exception) =>
        exception is QueryNestedTooDeeplyException or InsufficientExecutionStackException;

    /// <summary>
    /// Converts a depth refusal (see <see cref="IsDepthRefusal"/>) to a <see cref="QueryNestedTooDeeplyException"/>.
    /// </summary>
    /// <param name="exception">A depth refusal.</param>
    /// <returns>The refusal as a <see cref="QueryNestedTooDeeplyException"/>.</returns>
    public static QueryNestedTooDeeplyException AsNestedTooDeeply(Exception exception) =>
        exception as QueryNestedTooDeeplyException
        ?? new QueryNestedTooDeeplyException(
            QueryDepthLimit.ExecutionStack, QueryNestedTooDeeplyException.DefaultMessage, exception);

    /// <summary>
    /// Runs <paramref name="work"/> on a dedicated thread with a <see cref="DefaultStackSize"/> stack and waits
    /// for it; runs it inline when the current thread already is one.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">Synchronous work. It must not await or block on a task.</param>
    /// <returns>The result of <paramref name="work"/>.</returns>
    public static T Run<T>(Func<T> work) => Run(work, DefaultStackSize);

    /// <summary>
    /// Runs <paramref name="work"/> on a dedicated thread with a stack of <paramref name="stackSize"/> bytes
    /// and waits for it; runs it inline when the current thread already is one. If the stack cannot be
    /// reserved, the size is halved down to <see cref="MinimumFallbackStackSize"/>, and
    /// <see cref="CurrentThreadStackSize"/> reports the size obtained.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">Synchronous work. It must not await or block on a task.</param>
    /// <param name="stackSize">The stack size in bytes.</param>
    /// <returns>The result of <paramref name="work"/>.</returns>
    /// <remarks>An exception thrown by <paramref name="work"/> is rethrown on the calling thread with its stack trace.</remarks>
    public static T Run<T>(Func<T> work, int stackSize)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (t_stackSize > 0)
            return work();

        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = Start(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }, stackSize);
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a dedicated thread with a <see cref="DefaultStackSize"/> stack without
    /// blocking the caller; runs it inline when the current thread already is one.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">Synchronous work. It must not await or block on a task.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait: the returned task is cancelled at once. The work itself cannot be interrupted; it
    /// finishes on its thread and its result is dropped.
    /// </param>
    /// <returns>
    /// A task with the result. Its continuations never run on the dedicated thread. A cancelled wait throws
    /// <see cref="OperationCanceledException"/>.
    /// </returns>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromException<T>(new OperationCanceledException(cancellationToken));

        if (t_stackSize > 0)
        {
            try { return Task.FromResult(work()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Start(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        }, DefaultStackSize);
        return WaitAsync(completion.Task, cancellationToken);
    }

    // A cancelled wait surfaces as OperationCanceledException, as a cancelled call did before the hop.
    private static async Task<T> WaitAsync<T>(Task<T> work, CancellationToken cancellationToken)
    {
        try
        {
            return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested && !work.IsCompleted)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
    }

    private static Thread Start(Action body, int stackSize)
    {
        if (stackSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(stackSize), "The stack size must be positive.");

        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var size = stackSize;
        while (true)
        {
            var obtained = size;
            var thread = new Thread(() =>
            {
                t_stackSize = obtained;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
                body();
            }, obtained)
            {
                IsBackground = true,
                Name = "PPDS SQL parse"
            };
            try
            {
                thread.Start();
                return thread;
            }
            catch (OutOfMemoryException) when (size / 2 >= MinimumFallbackStackSize && size / 2 < stackSize)
            {
                // The reservation was refused (a 32-bit process, or a host that limits address space):
                // try a smaller stack. The caller's bound reads the size obtained.
                size /= 2;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNestedTooDeeply() =>
        throw new QueryNestedTooDeeplyException(QueryDepthLimit.ExecutionStack);
}
