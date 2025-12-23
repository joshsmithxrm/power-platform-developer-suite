using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PPDS.Dataverse.Progress
{
    /// <summary>
    /// Thread-safe progress tracker for bulk operations.
    /// Provides accurate rate calculation with both overall and instantaneous rates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class is designed for high-throughput scenarios with parallel batch processing.
    /// All public methods are thread-safe.
    /// </para>
    /// <para>
    /// Rate calculations:
    /// <list type="bullet">
    /// <item><see cref="ProgressSnapshot.OverallRatePerSecond"/>: Total records / total elapsed time. Stable, used for ETA.</item>
    /// <item><see cref="ProgressSnapshot.InstantRatePerSecond"/>: Based on a rolling window (default 30s). Reflects current performance.</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var tracker = new ProgressTracker(totalRecords);
    ///
    /// foreach (var batch in batches)
    /// {
    ///     var result = await ProcessBatchAsync(batch);
    ///     tracker.RecordProgress(result.SuccessCount, result.FailureCount);
    ///
    ///     var snapshot = tracker.GetSnapshot();
    ///     Console.WriteLine($"{snapshot.Processed}/{snapshot.Total} ({snapshot.PercentComplete:F1}%) " +
    ///                       $"@ {snapshot.InstantRatePerSecond:F0}/s, ETA: {snapshot.EstimatedRemaining:mm\\:ss}");
    /// }
    /// </code>
    /// </example>
    public sealed class ProgressTracker
    {
        private readonly Stopwatch _stopwatch;
        private readonly long _totalCount;
        private readonly TimeSpan _rollingWindowDuration;
        private readonly object _samplesLock = new();
        private readonly Queue<(long ticks, long processed)> _samples;

        private long _succeeded;
        private long _failed;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressTracker"/> class.
        /// </summary>
        /// <param name="totalCount">The total number of records to process.</param>
        /// <param name="rollingWindowSeconds">
        /// The duration of the rolling window for instantaneous rate calculation.
        /// Default is 30 seconds.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="totalCount"/> is negative, or
        /// <paramref name="rollingWindowSeconds"/> is less than 1.
        /// </exception>
        public ProgressTracker(long totalCount, int rollingWindowSeconds = 30)
        {
            if (totalCount < 0)
                throw new ArgumentOutOfRangeException(nameof(totalCount), "Total count cannot be negative.");
            if (rollingWindowSeconds < 1)
                throw new ArgumentOutOfRangeException(nameof(rollingWindowSeconds), "Rolling window must be at least 1 second.");

            _totalCount = totalCount;
            _rollingWindowDuration = TimeSpan.FromSeconds(rollingWindowSeconds);
            _samples = new Queue<(long, long)>();
            _stopwatch = Stopwatch.StartNew();

            // Add initial sample at t=0
            lock (_samplesLock)
            {
                _samples.Enqueue((0, 0));
            }
        }

        /// <summary>
        /// Gets the total count of records to process.
        /// </summary>
        public long TotalCount => _totalCount;

        /// <summary>
        /// Gets the current count of successfully processed records.
        /// </summary>
        public long Succeeded => Interlocked.Read(ref _succeeded);

        /// <summary>
        /// Gets the current count of failed records.
        /// </summary>
        public long Failed => Interlocked.Read(ref _failed);

        /// <summary>
        /// Gets the current count of processed records (succeeded + failed).
        /// </summary>
        public long Processed => Succeeded + Failed;

        /// <summary>
        /// Records progress for a batch of records.
        /// This method is thread-safe and can be called from multiple threads.
        /// </summary>
        /// <param name="successCount">Number of records that succeeded in this batch.</param>
        /// <param name="failureCount">Number of records that failed in this batch. Default is 0.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="successCount"/> or <paramref name="failureCount"/> is negative.
        /// </exception>
        public void RecordProgress(int successCount, int failureCount = 0)
        {
            if (successCount < 0)
                throw new ArgumentOutOfRangeException(nameof(successCount), "Success count cannot be negative.");
            if (failureCount < 0)
                throw new ArgumentOutOfRangeException(nameof(failureCount), "Failure count cannot be negative.");

            if (successCount > 0)
            {
                Interlocked.Add(ref _succeeded, successCount);
            }

            if (failureCount > 0)
            {
                Interlocked.Add(ref _failed, failureCount);
            }

            // Add sample for rolling window calculation
            var currentTicks = _stopwatch.ElapsedTicks;
            var processed = Processed;

            lock (_samplesLock)
            {
                _samples.Enqueue((currentTicks, processed));
                PruneSamples(currentTicks);
            }
        }

        /// <summary>
        /// Gets a snapshot of the current progress state.
        /// This method is thread-safe.
        /// </summary>
        /// <returns>An immutable snapshot of progress metrics.</returns>
        public ProgressSnapshot GetSnapshot()
        {
            var elapsed = _stopwatch.Elapsed;
            var succeeded = Interlocked.Read(ref _succeeded);
            var failed = Interlocked.Read(ref _failed);
            var processed = succeeded + failed;
            var remaining = Math.Max(0, _totalCount - processed);

            // Calculate overall rate (stable)
            var overallRate = elapsed.TotalSeconds > 0.1
                ? processed / elapsed.TotalSeconds
                : 0;

            // Calculate instant rate from rolling window
            var instantRate = CalculateInstantRate(processed);

            // Calculate ETA based on overall rate (more stable)
            var eta = overallRate > 0.001
                ? TimeSpan.FromSeconds(remaining / overallRate)
                : TimeSpan.MaxValue;

            // Cap ETA at a reasonable maximum (7 days)
            if (eta > TimeSpan.FromDays(7))
            {
                eta = TimeSpan.MaxValue;
            }

            return new ProgressSnapshot
            {
                Succeeded = succeeded,
                Failed = failed,
                Total = _totalCount,
                Elapsed = elapsed,
                OverallRatePerSecond = overallRate,
                InstantRatePerSecond = instantRate,
                EstimatedRemaining = eta
            };
        }

        /// <summary>
        /// Resets the tracker to its initial state.
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _succeeded, 0);
            Interlocked.Exchange(ref _failed, 0);

            lock (_samplesLock)
            {
                _samples.Clear();
                _samples.Enqueue((0, 0));
            }

            _stopwatch.Restart();
        }

        private double CalculateInstantRate(long currentProcessed)
        {
            lock (_samplesLock)
            {
                if (_samples.Count < 2)
                {
                    // Not enough samples, fall back to overall rate
                    var elapsed = _stopwatch.Elapsed;
                    return elapsed.TotalSeconds > 0.1 ? currentProcessed / elapsed.TotalSeconds : 0;
                }

                // Get oldest and newest samples in window
                var oldest = _samples.Peek();
                var currentTicks = _stopwatch.ElapsedTicks;

                // Calculate time span of the window
                var ticksPerSecond = Stopwatch.Frequency;
                var windowTicks = currentTicks - oldest.ticks;
                var windowSeconds = (double)windowTicks / ticksPerSecond;

                if (windowSeconds < 0.1)
                {
                    // Window too small, fall back to overall rate
                    var elapsed = _stopwatch.Elapsed;
                    return elapsed.TotalSeconds > 0.1 ? currentProcessed / elapsed.TotalSeconds : 0;
                }

                var recordsInWindow = currentProcessed - oldest.processed;
                return recordsInWindow / windowSeconds;
            }
        }

        private void PruneSamples(long currentTicks)
        {
            // Remove samples older than the rolling window
            var ticksPerSecond = Stopwatch.Frequency;
            var windowTicks = (long)(_rollingWindowDuration.TotalSeconds * ticksPerSecond);
            var cutoffTicks = currentTicks - windowTicks;

            while (_samples.Count > 1 && _samples.Peek().ticks < cutoffTicks)
            {
                _samples.Dequeue();
            }

            // Keep at least 2 samples for rate calculation, but limit memory
            // Keep max 1000 samples (for very high-frequency updates)
            while (_samples.Count > 1000)
            {
                _samples.Dequeue();
            }
        }
    }
}
