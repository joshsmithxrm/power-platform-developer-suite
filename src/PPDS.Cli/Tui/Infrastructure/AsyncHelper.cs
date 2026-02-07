using System.Runtime.CompilerServices;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Extension methods for fire-and-forget async patterns with centralized error reporting.
/// Replaces scattered #pragma warning disable PPDS013 blocks.
/// </summary>
internal static class AsyncHelper
{
    /// <summary>
    /// Runs a task fire-and-forget with error reporting via the error service.
    /// </summary>
    /// <param name="errorService">The error service to report failures to.</param>
    /// <param name="task">The task to run.</param>
    /// <param name="context">Context string for error reporting (e.g., "SwitchProfile").</param>
    /// <param name="caller">Auto-populated caller method name.</param>
    public static void FireAndForget(
        this ITuiErrorService errorService,
        Task task,
        string context,
        [CallerMemberName] string? caller = null)
    {
#pragma warning disable PPDS013 // Intentional fire-and-forget — this IS the centralized handler
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                errorService.ReportError(
                    "Background operation failed",
                    t.Exception,
                    $"{context} (from {caller})");
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }
}
