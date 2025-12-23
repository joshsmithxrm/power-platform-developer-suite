using System;

namespace PPDS.Dataverse.Progress
{
    /// <summary>
    /// Immutable snapshot of progress state at a point in time.
    /// </summary>
    public sealed class ProgressSnapshot
    {
        /// <summary>
        /// Gets the number of successfully processed records.
        /// </summary>
        public long Succeeded { get; init; }

        /// <summary>
        /// Gets the number of failed records.
        /// </summary>
        public long Failed { get; init; }

        /// <summary>
        /// Gets the total number of processed records (succeeded + failed).
        /// </summary>
        public long Processed => Succeeded + Failed;

        /// <summary>
        /// Gets the total number of records to process.
        /// </summary>
        public long Total { get; init; }

        /// <summary>
        /// Gets the remaining records to process.
        /// </summary>
        public long Remaining => Math.Max(0, Total - Processed);

        /// <summary>
        /// Gets the percentage complete (0-100).
        /// </summary>
        public double PercentComplete => Total > 0 ? (double)Processed / Total * 100 : 0;

        /// <summary>
        /// Gets the elapsed time since tracking started.
        /// </summary>
        public TimeSpan Elapsed { get; init; }

        /// <summary>
        /// Gets the processing rate (records per second) - total records divided by elapsed time.
        /// Use this rate for display and throughput reporting.
        /// </summary>
        public double RatePerSecond => OverallRatePerSecond;

        /// <summary>
        /// Gets the overall processing rate (records per second) since start.
        /// This is the stable rate used for ETA calculations. Same as <see cref="RatePerSecond"/>.
        /// </summary>
        public double OverallRatePerSecond { get; init; }

        /// <summary>
        /// Gets the instantaneous processing rate (records per second) based on a rolling window.
        /// <para>
        /// <b>Warning:</b> This value can fluctuate wildly in batch operations when multiple
        /// batches complete at once. For most display purposes, use <see cref="RatePerSecond"/> instead.
        /// </para>
        /// </summary>
        public double InstantRatePerSecond { get; init; }

        /// <summary>
        /// Gets the estimated time remaining based on overall rate.
        /// Returns <see cref="TimeSpan.MaxValue"/> if rate is zero.
        /// </summary>
        public TimeSpan EstimatedRemaining { get; init; }

        /// <summary>
        /// Gets the estimated completion time (UTC).
        /// </summary>
        public DateTime EstimatedCompletionUtc => EstimatedRemaining == TimeSpan.MaxValue
            ? DateTime.MaxValue
            : DateTime.UtcNow.Add(EstimatedRemaining);
    }
}
