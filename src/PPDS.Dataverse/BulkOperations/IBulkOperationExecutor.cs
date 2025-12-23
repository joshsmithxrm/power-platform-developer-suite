using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.Progress;

namespace PPDS.Dataverse.BulkOperations
{
    /// <summary>
    /// Executes bulk operations using modern Dataverse APIs.
    /// Provides CreateMultiple, UpdateMultiple, UpsertMultiple, and DeleteMultiple wrappers.
    /// </summary>
    public interface IBulkOperationExecutor
    {
        /// <summary>
        /// Creates multiple records using the CreateMultiple API.
        /// </summary>
        /// <param name="entityLogicalName">The entity logical name.</param>
        /// <param name="entities">The entities to create.</param>
        /// <param name="options">Bulk operation options.</param>
        /// <param name="progress">Optional progress reporter for tracking operation progress.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        Task<BulkOperationResult> CreateMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates multiple records using the UpdateMultiple API.
        /// </summary>
        /// <param name="entityLogicalName">The entity logical name.</param>
        /// <param name="entities">The entities to update.</param>
        /// <param name="options">Bulk operation options.</param>
        /// <param name="progress">Optional progress reporter for tracking operation progress.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        Task<BulkOperationResult> UpdateMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Upserts multiple records using the UpsertMultiple API.
        /// </summary>
        /// <param name="entityLogicalName">The entity logical name.</param>
        /// <param name="entities">The entities to upsert.</param>
        /// <param name="options">Bulk operation options.</param>
        /// <param name="progress">Optional progress reporter for tracking operation progress.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        Task<BulkOperationResult> UpsertMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes multiple records using the DeleteMultiple API.
        /// </summary>
        /// <param name="entityLogicalName">The entity logical name.</param>
        /// <param name="ids">The IDs of the records to delete.</param>
        /// <param name="options">Bulk operation options.</param>
        /// <param name="progress">Optional progress reporter for tracking operation progress.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        Task<BulkOperationResult> DeleteMultipleAsync(
            string entityLogicalName,
            IEnumerable<Guid> ids,
            BulkOperationOptions? options = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
