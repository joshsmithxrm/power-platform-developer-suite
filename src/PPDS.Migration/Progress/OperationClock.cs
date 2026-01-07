using System;
using System.Diagnostics;

namespace PPDS.Migration.Progress;

/// <summary>
/// Provides elapsed time since the current operation started.
/// Call <see cref="Start"/> at the beginning of each CLI command.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth for elapsed time in CLI output.
/// Both progress reporters and log formatters should read from this clock
/// to ensure consistent timestamps.
/// </para>
/// <para>
/// See ADR-0027 for architectural context.
/// </para>
/// </remarks>
public static class OperationClock
{
    private static readonly Stopwatch Stopwatch = new();

    /// <summary>
    /// Gets the elapsed time since <see cref="Start"/> was called.
    /// Returns <see cref="TimeSpan.Zero"/> if not started.
    /// </summary>
    public static TimeSpan Elapsed => Stopwatch.Elapsed;

    /// <summary>
    /// Starts or restarts the operation clock.
    /// Call at the beginning of each CLI command.
    /// </summary>
    public static void Start() => Stopwatch.Restart();
}
