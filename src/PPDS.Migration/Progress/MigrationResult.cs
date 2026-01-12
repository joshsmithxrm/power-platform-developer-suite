using System;
using System.Collections.Generic;
using PPDS.Dataverse.BulkOperations;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Result of a migration operation.
    /// </summary>
    public class MigrationResult
    {
        /// <summary>
        /// Gets or sets whether the operation was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the total records processed.
        /// </summary>
        public int RecordsProcessed { get; set; }

        /// <summary>
        /// Gets or sets the count of successful operations.
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// Gets or sets the count of failed operations.
        /// </summary>
        public int FailureCount { get; set; }

        /// <summary>
        /// Gets or sets the operation duration.
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Gets or sets the errors encountered.
        /// </summary>
        public IReadOnlyList<MigrationError> Errors { get; set; } = Array.Empty<MigrationError>();

        /// <summary>
        /// Gets or sets the number of records created during upsert operations.
        /// Only populated for upsert mode; null for create/update modes.
        /// </summary>
        public int? CreatedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of records updated during upsert operations.
        /// Only populated for upsert mode; null for create/update modes.
        /// </summary>
        public int? UpdatedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of M2M (many-to-many) relationship records processed.
        /// Only populated for import operations; null otherwise.
        /// </summary>
        public int? M2MCount { get; set; }

        /// <summary>
        /// Gets the average records per second.
        /// </summary>
        public double RecordsPerSecond => Duration.TotalSeconds > 0
            ? RecordsProcessed / Duration.TotalSeconds
            : 0;
    }

    /// <summary>
    /// Error information from a migration operation.
    /// Contains RecordId (GUID) for correlation but no record data to avoid PII exposure.
    /// </summary>
    public class MigrationError
    {
        /// <summary>
        /// Gets or sets the phase where the error occurred.
        /// </summary>
        public MigrationPhase Phase { get; set; }

        /// <summary>
        /// Gets or sets the entity logical name.
        /// </summary>
        public string? EntityLogicalName { get; set; }

        /// <summary>
        /// Gets or sets the record index (position in batch).
        /// </summary>
        public int? RecordIndex { get; set; }

        /// <summary>
        /// Gets or sets the record ID (GUID).
        /// This is a unique identifier, not PII.
        /// </summary>
        public Guid? RecordId { get; set; }

        /// <summary>
        /// Gets or sets the Dataverse error code.
        /// </summary>
        public int? ErrorCode { get; set; }

        /// <summary>
        /// Gets or sets a safe error message (no PII).
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp when the error occurred.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets diagnostics identifying which record(s) caused the batch failure.
        /// </summary>
        /// <remarks>
        /// Populated when a batch fails with a "Does Not Exist" error. Contains details
        /// about which record contains the problematic reference and the pattern detected.
        /// </remarks>
        public IReadOnlyList<BatchFailureDiagnostic>? Diagnostics { get; set; }
    }
}
