using System;
using System.Collections.Generic;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Progress event data for migration operations.
    /// </summary>
    public class ProgressEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the current phase of the migration.
        /// </summary>
        public MigrationPhase Phase { get; set; }

        /// <summary>
        /// Gets or sets the entity being processed (if applicable).
        /// </summary>
        public string? Entity { get; set; }

        /// <summary>
        /// Gets or sets the field being processed (for deferred fields).
        /// </summary>
        public string? Field { get; set; }

        /// <summary>
        /// Gets or sets the relationship being processed (for M2M).
        /// </summary>
        public string? Relationship { get; set; }

        /// <summary>
        /// Gets or sets the current tier number (for import).
        /// </summary>
        public int? TierNumber { get; set; }

        /// <summary>
        /// Gets or sets the current record/item count.
        /// </summary>
        public int Current { get; set; }

        /// <summary>
        /// Gets or sets the total record/item count.
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// Gets or sets the records per second rate.
        /// </summary>
        public double? RecordsPerSecond { get; set; }

        /// <summary>
        /// Gets or sets the estimated time remaining for the current entity/operation.
        /// </summary>
        public TimeSpan? EstimatedRemaining { get; set; }

        /// <summary>
        /// Gets or sets the number of records that succeeded in the current batch/phase.
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// Gets or sets the number of records that failed in the current batch/phase.
        /// </summary>
        public int FailureCount { get; set; }

        /// <summary>
        /// Gets or sets sample errors from the current batch for real-time visibility.
        /// Limited to a small number (typically 2) to avoid flooding output.
        /// </summary>
        public IReadOnlyList<MigrationError>? ErrorSamples { get; set; }

        /// <summary>
        /// Gets or sets a descriptive message.
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// Gets or sets the timestamp of this progress event.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets the percentage complete (0-100).
        /// </summary>
        public double PercentComplete => Total > 0 ? (double)Current / Total * 100 : 0;
    }
}
