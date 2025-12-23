using System;
using System.Diagnostics;
using System.Linq;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Progress reporter that writes human-readable output to the console.
    /// </summary>
    public class ConsoleProgressReporter : IProgressReporter
    {
        private const int MaxErrorsToDisplay = 10;

        private readonly Stopwatch _stopwatch = new();
        private string? _lastEntity;
        private int _lastProgress;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConsoleProgressReporter"/> class.
        /// </summary>
        public ConsoleProgressReporter()
        {
            _stopwatch.Start();
        }

        /// <inheritdoc />
        public void Report(ProgressEventArgs args)
        {
            var elapsed = _stopwatch.Elapsed;
            var prefix = $"[{elapsed:hh\\:mm\\:ss}]";

            switch (args.Phase)
            {
                case MigrationPhase.Analyzing:
                    Console.WriteLine($"{prefix} {args.Message}");
                    break;

                case MigrationPhase.Exporting:
                case MigrationPhase.Importing:
                    if (args.Entity != _lastEntity || args.Current == args.Total || ShouldUpdate(args.Current))
                    {
                        var phase = args.Phase == MigrationPhase.Exporting ? "Export" : "Import";
                        var tierInfo = args.TierNumber.HasValue ? $" (Tier {args.TierNumber})" : "";
                        var rps = args.RecordsPerSecond.HasValue ? $" @ {args.RecordsPerSecond:F1} rec/s" : "";
                        var pct = args.Total > 0 ? $" ({args.PercentComplete:F0}%)" : "";

                        // Show success/failure breakdown if there are failures
                        var failureInfo = args.FailureCount > 0
                            ? $" [{args.SuccessCount} ok, {args.FailureCount} failed]"
                            : "";

                        Console.WriteLine($"{prefix} [{phase}] {args.Entity}{tierInfo}: {args.Current:N0}/{args.Total:N0}{pct}{rps}{failureInfo}");

                        _lastEntity = args.Entity;
                        _lastProgress = args.Current;
                    }
                    break;

                case MigrationPhase.ProcessingDeferredFields:
                    // Handle cases where Field might be null or empty
                    if (!string.IsNullOrEmpty(args.Field) && args.Total > 0)
                    {
                        var successInfo = args.SuccessCount > 0 ? $" ({args.SuccessCount} updated)" : "";
                        Console.WriteLine($"{prefix} [Deferred] {args.Entity}.{args.Field}: {args.Current:N0}/{args.Total:N0}{successInfo}");
                    }
                    else if (!string.IsNullOrEmpty(args.Message))
                    {
                        Console.WriteLine($"{prefix} [Deferred] {args.Entity}: {args.Message}");
                    }
                    break;

                case MigrationPhase.ProcessingRelationships:
                    Console.WriteLine($"{prefix} [M2M] {args.Relationship}: {args.Current:N0}/{args.Total:N0}");
                    break;

                default:
                    if (!string.IsNullOrEmpty(args.Message))
                    {
                        Console.WriteLine($"{prefix} {args.Message}");
                    }
                    break;
            }
        }

        /// <inheritdoc />
        public void Complete(MigrationResult result)
        {
            _stopwatch.Stop();
            Console.WriteLine();
            Console.WriteLine(new string('=', 60));

            if (result.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Migration Completed Successfully");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Migration Completed with Errors");
            }
            Console.ResetColor();

            Console.WriteLine(new string('=', 60));
            Console.WriteLine($"Duration:    {result.Duration:hh\\:mm\\:ss}");
            Console.WriteLine($"Succeeded:   {result.SuccessCount:N0}");

            if (result.FailureCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed:      {result.FailureCount:N0}");
                Console.ResetColor();
            }

            Console.WriteLine($"Throughput:  {result.RecordsPerSecond:F1} records/second");

            // Display error details if available
            if (result.Errors?.Count > 0)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Errors ({result.Errors.Count}):");

                foreach (var error in result.Errors.Take(MaxErrorsToDisplay))
                {
                    var entity = !string.IsNullOrEmpty(error.EntityLogicalName) ? $"{error.EntityLogicalName}: " : "";
                    var index = error.RecordIndex.HasValue ? $"[{error.RecordIndex}] " : "";
                    Console.WriteLine($"  - {entity}{index}{error.Message}");
                }

                if (result.Errors.Count > MaxErrorsToDisplay)
                {
                    Console.WriteLine($"  ... and {result.Errors.Count - MaxErrorsToDisplay} more errors");
                }
                Console.ResetColor();
            }

            Console.WriteLine();
        }

        /// <inheritdoc />
        public void Error(Exception exception, string? context = null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            if (!string.IsNullOrEmpty(context))
            {
                Console.Error.WriteLine($"Context: {context}");
            }
            Console.ResetColor();
        }

        private bool ShouldUpdate(int current)
        {
            // Update every 1000 records or 100 records, whichever comes first
            return current - _lastProgress >= 1000 || current - _lastProgress >= 100;
        }
    }
}
