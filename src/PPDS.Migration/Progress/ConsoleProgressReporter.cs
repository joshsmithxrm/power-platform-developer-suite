using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Progress reporter that writes human-readable output to stderr.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class is not thread-safe. It is designed for single-threaded CLI usage
    /// where progress events are reported sequentially.
    /// </para>
    /// <para>
    /// Progress is written to stderr to keep stdout clean for command results,
    /// enabling piping (e.g., <c>ppds data export | jq</c>) without interference.
    /// </para>
    /// </remarks>
    public class ConsoleProgressReporter : IProgressReporter
    {
        private const int MaxErrorsToDisplay = 10;
        private const int MaxSuggestionsToDisplay = 3;

        private readonly Stopwatch _stopwatch = new();
        private string? _lastEntity;
        private int _lastProgress;

        /// <inheritdoc />
        public string OperationName { get; set; } = "Operation";

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
            var prefix = $"[+{elapsed:hh\\:mm\\:ss\\.fff}]";

            switch (args.Phase)
            {
                case MigrationPhase.Analyzing:
                    Console.Error.WriteLine($"{prefix} {args.Message}");
                    break;

                case MigrationPhase.Exporting:
                case MigrationPhase.Importing:
                    // Handle message-only events (e.g., "Writing output file...")
                    if (!string.IsNullOrEmpty(args.Message) && string.IsNullOrEmpty(args.Entity))
                    {
                        Console.Error.WriteLine($"{prefix} {args.Message}");
                        break;
                    }

                    // Handle entity progress events - skip if no entity specified
                    if (string.IsNullOrEmpty(args.Entity))
                    {
                        break;
                    }

                    // Use unique key for M2M relationships to track progress separately
                    var progressKey = string.IsNullOrEmpty(args.Relationship)
                        ? args.Entity
                        : $"{args.Entity}:{args.Relationship}";

                    if (progressKey != _lastEntity || args.Current == args.Total || ShouldUpdate(args.Current))
                    {
                        var phase = args.Phase == MigrationPhase.Exporting ? "Export" : "Import";
                        var tierInfo = args.TierNumber.HasValue ? $" (Tier {args.TierNumber})" : "";
                        var relInfo = !string.IsNullOrEmpty(args.Relationship) ? $" M2M {args.Relationship}" : "";
                        var rps = args.RecordsPerSecond.HasValue ? $" @ {args.RecordsPerSecond:F1} rec/s" : "";
                        var pct = args.Total > 0 ? $" ({args.PercentComplete:F0}%)" : "";
                        var eta = args.EstimatedRemaining.HasValue ? $" | ETA: {FormatEta(args.EstimatedRemaining.Value)}" : "";

                        // Show success/failure breakdown if there are failures
                        var failureInfo = args.FailureCount > 0
                            ? $" [{args.SuccessCount} ok, {args.FailureCount} failed]"
                            : "";

                        Console.Error.WriteLine($"{prefix} [{phase}] {args.Entity}{relInfo}{tierInfo}: {args.Current:N0}/{args.Total:N0}{pct}{rps}{eta}{failureInfo}");

                        // Show sample errors for real-time visibility
                        if (args.FailureCount > 0 && args.ErrorSamples?.Count > 0)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            foreach (var error in args.ErrorSamples.Take(2))
                            {
                                var idPrefix = error.RecordId.HasValue
                                    ? $"[{error.RecordId.Value.ToString("D")[..8]}...] "
                                    : "";
                                var shortMessage = error.Message.Length > 80
                                    ? error.Message[..77] + "..."
                                    : error.Message;
                                Console.Error.WriteLine($"        {idPrefix}{shortMessage}");
                            }
                            Console.ResetColor();
                        }

                        _lastEntity = progressKey;
                        _lastProgress = args.Current;
                    }
                    break;

                case MigrationPhase.ProcessingDeferredFields:
                    // Handle cases where Field might be null or empty
                    if (!string.IsNullOrEmpty(args.Field) && args.Total > 0)
                    {
                        var successInfo = args.SuccessCount > 0 ? $" ({args.SuccessCount} updated)" : "";
                        Console.Error.WriteLine($"{prefix} [Deferred] {args.Entity}.{args.Field}: {args.Current:N0}/{args.Total:N0}{successInfo}");
                    }
                    else if (!string.IsNullOrEmpty(args.Message))
                    {
                        Console.Error.WriteLine($"{prefix} [Deferred] {args.Entity}: {args.Message}");
                    }
                    break;

                case MigrationPhase.ProcessingRelationships:
                    Console.Error.WriteLine($"{prefix} [M2M] {args.Relationship}: {args.Current:N0}/{args.Total:N0}");
                    break;

                default:
                    if (!string.IsNullOrEmpty(args.Message))
                    {
                        Console.Error.WriteLine($"{prefix} {args.Message}");
                    }
                    break;
            }
        }

        /// <inheritdoc />
        public void Complete(MigrationResult result)
        {
            _stopwatch.Stop();
            Console.Error.WriteLine();

            // Header line: "Export succeeded." or "Export completed with errors."
            if (result.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Error.WriteLine($"{OperationName} succeeded.");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine($"{OperationName} completed with errors.");
            }
            Console.ResetColor();

            // Summary line: "    42,366 records in 00:00:08 (4,774.5 rec/s)"
            Console.Error.WriteLine($"    {result.SuccessCount:N0} record(s) in {result.Duration:hh\\:mm\\:ss} ({result.RecordsPerSecond:F1} rec/s)");

            // Show created/updated breakdown for upsert operations
            if (result.CreatedCount.HasValue && result.UpdatedCount.HasValue)
            {
                Console.Error.WriteLine($"        Created: {result.CreatedCount:N0} | Updated: {result.UpdatedCount:N0}");
            }

            // Error count if any
            if (result.FailureCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"    {result.FailureCount:N0} Error(s)");
                Console.ResetColor();

                // Show per-entity breakdown
                var byEntity = result.Errors
                    .Where(e => !string.IsNullOrEmpty(e.EntityLogicalName))
                    .GroupBy(e => e.EntityLogicalName!)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .ToList();

                if (byEntity.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Failures by entity:");
                    foreach (var group in byEntity)
                    {
                        Console.Error.WriteLine($"  {group.Key}: {group.Count():N0} failed");
                        // Show sample RecordIds if available
                        var sampleIds = group
                            .Where(e => e.RecordId.HasValue)
                            .Take(3)
                            .Select(e => e.RecordId!.Value.ToString("D")[..8] + "...");
                        if (sampleIds.Any())
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Error.WriteLine($"    Sample IDs: {string.Join(", ", sampleIds)}");
                            Console.ResetColor();
                        }
                    }
                }
            }
            else
            {
                Console.Error.WriteLine($"    0 Error(s)");
            }

            // Display error details if available
            if (result.Errors?.Count > 0)
            {
                // Detect patterns in errors for actionable suggestions
                var patterns = DetectErrorPatterns(result.Errors);
                var suggestions = GetActionableSuggestions(patterns);

                Console.Error.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;

                // Show pattern summary if most errors share the same cause
                if (patterns.Count > 0)
                {
                    var topPattern = patterns.First();
                    if (topPattern.Value >= result.Errors.Count * 0.8) // 80%+ same error
                    {
                        Console.Error.WriteLine($"Error Pattern: {topPattern.Value:N0} of {result.Errors.Count:N0} errors share the same cause:");
                        Console.Error.WriteLine($"  {GetPatternDescription(topPattern.Key)}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Errors ({result.Errors.Count:N0}):");
                        foreach (var error in result.Errors.Take(MaxErrorsToDisplay))
                        {
                            var entity = !string.IsNullOrEmpty(error.EntityLogicalName) ? $"{error.EntityLogicalName}: " : "";
                            var index = error.RecordIndex.HasValue ? $"[{error.RecordIndex}] " : "";
                            Console.Error.WriteLine($"  - {entity}{index}{error.Message}");
                        }

                        if (result.Errors.Count > MaxErrorsToDisplay)
                        {
                            Console.Error.WriteLine($"  ... and {result.Errors.Count - MaxErrorsToDisplay} more errors");
                        }
                    }
                }
                else
                {
                    Console.Error.WriteLine($"Errors ({result.Errors.Count:N0}):");
                    foreach (var error in result.Errors.Take(MaxErrorsToDisplay))
                    {
                        var entity = !string.IsNullOrEmpty(error.EntityLogicalName) ? $"{error.EntityLogicalName}: " : "";
                        var index = error.RecordIndex.HasValue ? $"[{error.RecordIndex}] " : "";
                        Console.Error.WriteLine($"  - {entity}{index}{error.Message}");
                    }

                    if (result.Errors.Count > MaxErrorsToDisplay)
                    {
                        Console.Error.WriteLine($"  ... and {result.Errors.Count - MaxErrorsToDisplay} more errors");
                    }
                }
                Console.ResetColor();

                // Show actionable suggestions
                if (suggestions.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Error.WriteLine("Suggested fixes:");
                    foreach (var suggestion in suggestions.Take(MaxSuggestionsToDisplay))
                    {
                        Console.Error.WriteLine($"  -> {suggestion}");
                    }
                    Console.ResetColor();
                }
            }

            Console.Error.WriteLine();
        }

        /// <summary>
        /// Detects common error patterns in the error list.
        /// </summary>
        private static Dictionary<string, int> DetectErrorPatterns(IReadOnlyList<MigrationError> errors)
        {
            var patterns = new Dictionary<string, int>();

            foreach (var error in errors)
            {
                var patternKey = ClassifyError(error.Message);
                if (!string.IsNullOrEmpty(patternKey))
                {
                    patterns.TryGetValue(patternKey, out var count);
                    patterns[patternKey] = count + 1;
                }
            }

            // Sort by count descending
            return patterns
                .OrderByDescending(p => p.Value)
                .ToDictionary(p => p.Key, p => p.Value);
        }

        /// <summary>
        /// Classifies an error message into a pattern category.
        /// </summary>
        private static string ClassifyError(string message)
        {
            if (string.IsNullOrEmpty(message))
                return string.Empty;

            // systemuser/team does not exist - common cross-environment issue
            if (message.Contains("systemuser", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("Does Not Exist", StringComparison.OrdinalIgnoreCase))
            {
                return "MISSING_USER";
            }

            if (message.Contains("team", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("Does Not Exist", StringComparison.OrdinalIgnoreCase))
            {
                return "MISSING_TEAM";
            }

            // Record does not exist (lookup reference)
            if (Regex.IsMatch(message, @"Entity '\w+' With Id = .+ Does Not Exist", RegexOptions.IgnoreCase))
            {
                return "MISSING_REFERENCE";
            }

            // Duplicate record
            if (message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return "DUPLICATE_RECORD";
            }

            // Permission/security
            if (message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("privilege", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("access denied", StringComparison.OrdinalIgnoreCase))
            {
                return "PERMISSION_DENIED";
            }

            // Required field missing
            if (message.Contains("required", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("field", StringComparison.OrdinalIgnoreCase))
            {
                return "REQUIRED_FIELD";
            }

            return string.Empty;
        }

        /// <summary>
        /// Gets a human-readable description for an error pattern.
        /// </summary>
        private static string GetPatternDescription(string patternKey) => patternKey switch
        {
            "MISSING_USER" => "Referenced systemuser (owner/createdby/modifiedby) does not exist in target environment",
            "MISSING_TEAM" => "Referenced team does not exist in target environment",
            "MISSING_REFERENCE" => "Referenced record does not exist in target environment",
            "DUPLICATE_RECORD" => "Record already exists (duplicate detected)",
            "PERMISSION_DENIED" => "Insufficient permissions to create/update records",
            "REQUIRED_FIELD" => "Required field is missing or null",
            _ => "Unknown error pattern"
        };

        /// <summary>
        /// Gets actionable suggestions based on detected error patterns.
        /// </summary>
        private static List<string> GetActionableSuggestions(Dictionary<string, int> patterns)
        {
            var suggestions = new List<string>();

            foreach (var pattern in patterns.Keys)
            {
                switch (pattern)
                {
                    case "MISSING_USER":
                    case "MISSING_TEAM":
                        suggestions.Add("Use --strip-owner-fields to remove ownership references and let Dataverse assign the current user");
                        suggestions.Add("Or provide a --user-mapping file to remap user references to valid users in the target");
                        break;

                    case "MISSING_REFERENCE":
                        suggestions.Add("Ensure referenced records exist in target environment before importing dependent records");
                        suggestions.Add("Check that the data file includes all required parent records");
                        break;

                    case "DUPLICATE_RECORD":
                        suggestions.Add("Use --mode Update to update existing records instead of creating duplicates");
                        suggestions.Add("Or use --mode Upsert to create-or-update based on record ID");
                        break;

                    case "PERMISSION_DENIED":
                        suggestions.Add("Verify the service principal has sufficient privileges in the target environment");
                        suggestions.Add("Check System Administrator or appropriate security role assignment");
                        break;

                    case "REQUIRED_FIELD":
                        suggestions.Add("Ensure required fields are populated in the source data");
                        suggestions.Add("Check entity metadata for required field definitions");
                        break;
                }
            }

            // Deduplicate suggestions
            return suggestions.Distinct().ToList();
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

        /// <inheritdoc />
        public void Reset()
        {
            _stopwatch.Restart();
            _lastEntity = null;
            _lastProgress = 0;
        }

        private bool ShouldUpdate(int current)
        {
            // Update every 1000 records or 100 records, whichever comes first
            return current - _lastProgress >= 1000 || current - _lastProgress >= 100;
        }

        /// <summary>
        /// Formats a TimeSpan for ETA display, handling hour+ durations correctly.
        /// </summary>
        private static string FormatEta(TimeSpan eta)
        {
            if (eta.TotalHours >= 1)
            {
                return $"{(int)eta.TotalHours}:{eta.Minutes:D2}:{eta.Seconds:D2}";
            }
            return $"{eta.Minutes}:{eta.Seconds:D2}";
        }
    }
}
