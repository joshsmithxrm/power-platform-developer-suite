using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Migration.Import;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Writes comprehensive error reports for import operations.
    /// </summary>
    public static class ErrorReportWriter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new TimeSpanConverter() }
        };

        /// <summary>
        /// Writes an error report to the specified file.
        /// </summary>
        /// <param name="filePath">The output file path.</param>
        /// <param name="result">The import result.</param>
        /// <param name="sourceFile">The source data file path.</param>
        /// <param name="targetEnvironment">The target environment URL.</param>
        /// <param name="executionContext">Optional execution context for diagnostic info.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public static async Task WriteAsync(
            string filePath,
            ImportResult result,
            string? sourceFile,
            string? targetEnvironment,
            ImportExecutionContext? executionContext = null,
            CancellationToken cancellationToken = default)
        {
            var report = BuildReport(result, sourceFile, targetEnvironment, executionContext);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
        }

        /// <summary>
        /// Builds an error report from an import result.
        /// </summary>
        private static ImportErrorReport BuildReport(
            ImportResult result,
            string? sourceFile,
            string? targetEnvironment,
            ImportExecutionContext? executionContext)
        {
            var errors = result.Errors ?? Array.Empty<MigrationError>();
            var patterns = DetectErrorPatterns(errors);

            var report = new ImportErrorReport
            {
                GeneratedAt = DateTime.UtcNow,
                SourceFile = sourceFile,
                TargetEnvironment = targetEnvironment,
                ExecutionContext = executionContext,
                Summary = new ImportErrorSummary
                {
                    TotalRecords = result.RecordsImported + result.RecordsUpdated + errors.Count,
                    SuccessCount = result.RecordsImported + result.RecordsUpdated,
                    FailureCount = errors.Count,
                    Duration = result.Duration,
                    ErrorPatterns = patterns
                }
            };

            // Build per-entity summaries
            var byEntity = errors
                .Where(e => !string.IsNullOrEmpty(e.EntityLogicalName))
                .GroupBy(e => e.EntityLogicalName!)
                .OrderByDescending(g => g.Count());

            foreach (var group in byEntity)
            {
                var entityResult = result.EntityResults?.FirstOrDefault(
                    r => r.EntityLogicalName == group.Key);

                var topErrors = group
                    .Select(e => e.Message)
                    .Where(m => !string.IsNullOrEmpty(m))
                    .GroupBy(m => m)
                    .OrderByDescending(g => g.Count())
                    .Take(3)
                    .Select(g => g.Key)
                    .ToList();

                report.EntitiesSummary.Add(new EntityErrorSummary
                {
                    EntityLogicalName = group.Key,
                    TotalRecords = entityResult?.RecordCount ?? group.Count(),
                    FailureCount = group.Count(),
                    TopErrors = topErrors
                });
            }

            // Build detailed error list
            foreach (var error in errors)
            {
                var pattern = ClassifyError(error.Message);
                report.Errors.Add(new DetailedError
                {
                    EntityLogicalName = error.EntityLogicalName ?? string.Empty,
                    RecordId = error.RecordId,
                    RecordIndex = error.RecordIndex,
                    ErrorCode = error.ErrorCode,
                    Message = error.Message,
                    Pattern = !string.IsNullOrEmpty(pattern) ? pattern : null,
                    Timestamp = error.Timestamp,
                    Diagnostics = error.Diagnostics
                });
            }

            // Build retry manifest
            var failedByEntity = errors
                .Where(e => e.RecordId.HasValue && !string.IsNullOrEmpty(e.EntityLogicalName))
                .GroupBy(e => e.EntityLogicalName!)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.RecordId!.Value).Distinct().ToList());

            if (failedByEntity.Count > 0)
            {
                report.RetryManifest = new RetryManifest
                {
                    GeneratedAt = DateTime.UtcNow,
                    SourceFile = sourceFile,
                    FailedRecordsByEntity = failedByEntity
                };
            }

            return report;
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

            return patterns
                .OrderByDescending(p => p.Value)
                .ToDictionary(p => p.Key, p => p.Value);
        }

        /// <summary>
        /// Classifies an error message into a pattern category.
        /// </summary>
        internal static string ClassifyError(string message)
        {
            if (string.IsNullOrEmpty(message))
                return string.Empty;

            // Pool exhaustion - our own infrastructure error
            if (message.Contains("pool exhausted", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("PoolExhaustedException", StringComparison.OrdinalIgnoreCase))
            {
                return "POOL_EXHAUSTION";
            }

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

            // Self-referential parent doesn't exist
            if (Regex.IsMatch(message, @"\w+ With Ids? = .+ Do(es)? Not Exist", RegexOptions.IgnoreCase))
            {
                return "MISSING_PARENT";
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

            // Bulk operation not supported
            if (message.Contains("not enabled on the entity", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Multiple is not supported", StringComparison.OrdinalIgnoreCase))
            {
                return "BULK_NOT_SUPPORTED";
            }

            return string.Empty;
        }

        /// <summary>
        /// Custom TimeSpan converter for JSON serialization.
        /// Serializes as ISO 8601 duration string.
        /// </summary>
        private class TimeSpanConverter : JsonConverter<TimeSpan>
        {
            public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var value = reader.GetString();
                return value != null ? TimeSpan.Parse(value) : TimeSpan.Zero;
            }

            public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString(@"hh\:mm\:ss\.fff"));
            }
        }
    }
}
