using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.BulkOperations
{
    /// <summary>
    /// Result of a bulk operation.
    /// </summary>
    public record BulkOperationResult
    {
        /// <summary>
        /// Gets the number of successful operations.
        /// </summary>
        public int SuccessCount { get; init; }

        /// <summary>
        /// Gets the number of failed operations.
        /// </summary>
        public int FailureCount { get; init; }

        /// <summary>
        /// Gets the errors that occurred during the operation.
        /// </summary>
        public IReadOnlyList<BulkOperationError> Errors { get; init; } = Array.Empty<BulkOperationError>();

        /// <summary>
        /// Gets the duration of the operation.
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Gets a value indicating whether all operations succeeded.
        /// </summary>
        public bool IsSuccess => FailureCount == 0;

        /// <summary>
        /// Gets the total number of operations attempted.
        /// </summary>
        public int TotalCount => SuccessCount + FailureCount;

        /// <summary>
        /// Gets the IDs of successfully created records from CreateMultiple operations.
        /// Only populated for create operations; null for update/upsert/delete.
        /// </summary>
        public IReadOnlyList<Guid>? CreatedIds { get; init; }

        /// <summary>
        /// Gets the number of records that were created during an UpsertMultiple operation.
        /// Only populated for upsert operations; null for create/update/delete.
        /// </summary>
        public int? CreatedCount { get; init; }

        /// <summary>
        /// Gets the number of records that were updated during an UpsertMultiple operation.
        /// Only populated for upsert operations; null for create/update/delete.
        /// </summary>
        public int? UpdatedCount { get; init; }
    }

    /// <summary>
    /// Error details for a failed record in a bulk operation.
    /// </summary>
    public class BulkOperationError
    {
        /// <summary>
        /// Gets the index of the record in the input collection.
        /// </summary>
        public int Index { get; init; }

        /// <summary>
        /// Gets the record ID, if available.
        /// </summary>
        public Guid? RecordId { get; init; }

        /// <summary>
        /// Gets the error code.
        /// </summary>
        public int ErrorCode { get; init; }

        /// <summary>
        /// Gets the error message.
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// Gets the field name that caused the error, if identifiable from the error message.
        /// Useful for debugging lookup failures and required field errors.
        /// </summary>
        public string? FieldName { get; init; }

        /// <summary>
        /// Gets a description of the field value that caused the error (sanitized for logging).
        /// For EntityReference: "{LogicalName}:{Id}". For other types: type name only.
        /// Does not contain actual data values to avoid PII in logs.
        /// </summary>
        public string? FieldValueDescription { get; init; }

        /// <summary>
        /// Gets diagnostics identifying which record(s) caused the batch failure.
        /// </summary>
        /// <remarks>
        /// Populated when a batch fails with a "Does Not Exist" error. Contains details
        /// about which record contains the problematic reference and the pattern detected.
        /// </remarks>
        public IReadOnlyList<BatchFailureDiagnostic>? Diagnostics { get; init; }
    }
}
