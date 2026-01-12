using System;
using PPDS.Dataverse.BulkOperations;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Helper methods for bulk operation handling.
    /// </summary>
    internal static class BulkOperationHelper
    {
        /// <summary>
        /// Determines if a bulk operation failure indicates the entity doesn't support bulk operations.
        /// </summary>
        /// <param name="result">The bulk operation result to check.</param>
        /// <param name="totalRecords">The total number of records in the operation.</param>
        /// <returns>True if the failure indicates bulk operations are not supported for this entity.</returns>
        /// <remarks>
        /// Some entities don't support bulk operations (CreateMultiple, UpdateMultiple, etc.).
        /// Error messages vary by entity:
        /// - "is not enabled on the entity" (team)
        /// - "does not support entities of type" (queue)
        /// </remarks>
        public static bool IsBulkNotSupportedFailure(BulkOperationResult result, int totalRecords)
        {
            // Only consider it a "not supported" failure if ALL records failed
            if (result.FailureCount != totalRecords || result.Errors.Count == 0)
                return false;

            // Check if first error indicates bulk operation not supported
            // Different entities return different error messages
            var firstError = result.Errors[0];
            var message = firstError.Message;
            if (string.IsNullOrEmpty(message))
                return false;

            const string NotEnabledOnEntityError = "is not enabled on the entity";
            const string DoesNotSupportEntitiesTypeError = "does not support entities of type";

            return message.Contains(NotEnabledOnEntityError, StringComparison.OrdinalIgnoreCase) ||
                   message.Contains(DoesNotSupportEntitiesTypeError, StringComparison.OrdinalIgnoreCase);
        }
    }
}
