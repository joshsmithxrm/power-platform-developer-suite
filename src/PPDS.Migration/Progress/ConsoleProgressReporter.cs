using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Progress reporter that writes human-readable output to the console.
    /// </summary>
    public class ConsoleProgressReporter : IProgressReporter
    {
        private const int MaxErrorsToDisplay = 10;
        private const int MaxSuggestionsToDisplay = 3;

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
                // Detect patterns in errors for actionable suggestions
                var patterns = DetectErrorPatterns(result.Errors);
                var suggestions = GetActionableSuggestions(patterns);

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;

                // Show pattern summary if most errors share the same cause
                if (patterns.Count > 0)
                {
                    var topPattern = patterns.First();
                    if (topPattern.Value >= result.Errors.Count * 0.8) // 80%+ same error
                    {
                        Console.WriteLine($"Error Pattern: {topPattern.Value:N0} of {result.Errors.Count:N0} errors share the same cause:");
                        Console.WriteLine($"  {GetPatternDescription(topPattern.Key)}");
                    }
                    else
                    {
                        Console.WriteLine($"Errors ({result.Errors.Count:N0}):");
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
                    }
                }
                else
                {
                    Console.WriteLine($"Errors ({result.Errors.Count:N0}):");
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
                }
                Console.ResetColor();

                // Show actionable suggestions
                if (suggestions.Count > 0)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("Suggested fixes:");
                    foreach (var suggestion in suggestions.Take(MaxSuggestionsToDisplay))
                    {
                        Console.WriteLine($"  -> {suggestion}");
                    }
                    Console.ResetColor();
                }
            }

            Console.WriteLine();
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

        private bool ShouldUpdate(int current)
        {
            // Update every 1000 records or 100 records, whichever comes first
            return current - _lastProgress >= 1000 || current - _lastProgress >= 100;
        }
    }
}
