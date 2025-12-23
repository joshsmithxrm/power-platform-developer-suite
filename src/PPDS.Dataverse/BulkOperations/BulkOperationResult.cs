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
    }
}
