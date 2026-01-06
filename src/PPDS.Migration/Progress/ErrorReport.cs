using System;
using System.Collections.Generic;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Comprehensive error report for import operations.
    /// </summary>
    public class ImportErrorReport
    {
        /// <summary>
        /// Gets or sets the schema version of this report.
        /// </summary>
        public string Version { get; set; } = "1.0";

        /// <summary>
        /// Gets or sets the timestamp when this report was generated.
        /// </summary>
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the source data file path.
        /// </summary>
        public string? SourceFile { get; set; }

        /// <summary>
        /// Gets or sets the target environment URL.
        /// </summary>
        public string? TargetEnvironment { get; set; }

        /// <summary>
        /// Gets or sets the import summary statistics.
        /// </summary>
        public ImportErrorSummary Summary { get; set; } = new();

        /// <summary>
        /// Gets or sets the per-entity error summaries.
        /// </summary>
        public List<EntityErrorSummary> EntitiesSummary { get; set; } = new();

        /// <summary>
        /// Gets or sets all detailed errors.
        /// </summary>
        public List<DetailedError> Errors { get; set; } = new();

        /// <summary>
        /// Gets or sets the retry manifest for failed records.
        /// </summary>
        public RetryManifest? RetryManifest { get; set; }
    }

    /// <summary>
    /// Summary statistics for an import operation.
    /// </summary>
    public class ImportErrorSummary
    {
        /// <summary>
        /// Gets or sets the total number of records attempted.
        /// </summary>
        public int TotalRecords { get; set; }

        /// <summary>
        /// Gets or sets the number of successful records.
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// Gets or sets the number of failed records.
        /// </summary>
        public int FailureCount { get; set; }

        /// <summary>
        /// Gets or sets the total import duration.
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Gets or sets error patterns and their counts.
        /// Keys are pattern names (e.g., "MISSING_USER"), values are occurrence counts.
        /// </summary>
        public Dictionary<string, int> ErrorPatterns { get; set; } = new();
    }

    /// <summary>
    /// Per-entity error summary.
    /// </summary>
    public class EntityErrorSummary
    {
        /// <summary>
        /// Gets or sets the entity logical name.
        /// </summary>
        public string EntityLogicalName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the total records for this entity.
        /// </summary>
        public int TotalRecords { get; set; }

        /// <summary>
        /// Gets or sets the failure count for this entity.
        /// </summary>
        public int FailureCount { get; set; }

        /// <summary>
        /// Gets or sets the most common error messages for this entity.
        /// </summary>
        public List<string> TopErrors { get; set; } = new();
    }

    /// <summary>
    /// Detailed error information for a single record.
    /// </summary>
    public class DetailedError
    {
        /// <summary>
        /// Gets or sets the entity logical name.
        /// </summary>
        public string EntityLogicalName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the record ID (GUID).
        /// </summary>
        public Guid? RecordId { get; set; }

        /// <summary>
        /// Gets or sets the record index (position in batch).
        /// </summary>
        public int? RecordIndex { get; set; }

        /// <summary>
        /// Gets or sets the Dataverse error code.
        /// </summary>
        public int? ErrorCode { get; set; }

        /// <summary>
        /// Gets or sets the error message.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the detected error pattern (e.g., "MISSING_USER").
        /// </summary>
        public string? Pattern { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the error occurred.
        /// </summary>
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Manifest of failed records for retry operations.
    /// Can be used as input filter for subsequent import.
    /// </summary>
    public class RetryManifest
    {
        /// <summary>
        /// Gets or sets the schema version of this manifest.
        /// </summary>
        public string Version { get; set; } = "1.0";

        /// <summary>
        /// Gets or sets the timestamp when this manifest was generated.
        /// </summary>
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the source file this manifest was generated from.
        /// </summary>
        public string? SourceFile { get; set; }

        /// <summary>
        /// Gets or sets the failed record IDs grouped by entity.
        /// </summary>
        public Dictionary<string, List<Guid>> FailedRecordsByEntity { get; set; } = new();
    }
}
