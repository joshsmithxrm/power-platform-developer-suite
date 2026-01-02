using System;
using System.Collections.Generic;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Result from a single import phase.
    /// </summary>
    public class PhaseResult
    {
        /// <summary>
        /// Gets or sets whether the phase completed successfully (no errors).
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Gets or sets the total number of records processed in this phase.
        /// </summary>
        public int RecordsProcessed { get; init; }

        /// <summary>
        /// Gets or sets the number of successfully processed records.
        /// </summary>
        public int SuccessCount { get; init; }

        /// <summary>
        /// Gets or sets the number of failed records.
        /// </summary>
        public int FailureCount { get; init; }

        /// <summary>
        /// Gets or sets the duration of this phase.
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Gets or sets the errors encountered during this phase.
        /// </summary>
        public IReadOnlyList<MigrationError> Errors { get; init; } = Array.Empty<MigrationError>();

        /// <summary>
        /// Creates a successful result with no records processed (phase skipped).
        /// </summary>
        public static PhaseResult Skipped() => new()
        {
            Success = true,
            RecordsProcessed = 0,
            SuccessCount = 0,
            FailureCount = 0,
            Duration = TimeSpan.Zero
        };

        /// <summary>
        /// Creates a successful result with the specified counts.
        /// </summary>
        /// <param name="processed">Number of records processed.</param>
        /// <param name="duration">Duration of the phase.</param>
        public static PhaseResult Succeeded(int processed, TimeSpan duration) => new()
        {
            Success = true,
            RecordsProcessed = processed,
            SuccessCount = processed,
            FailureCount = 0,
            Duration = duration
        };
    }
}
