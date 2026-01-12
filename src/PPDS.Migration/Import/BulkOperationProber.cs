using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Probes entities to detect bulk operation support and executes appropriate strategy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Some Dataverse entities (like team, queue) don't support CreateMultiple/UpdateMultiple/UpsertMultiple.
    /// This class probes with a single record first, and if bulk ops aren't supported, falls back to
    /// individual operations for all records (including the probe record).
    /// </para>
    /// <para>
    /// The probe result is cached per entity for the lifetime of the prober instance, avoiding
    /// repeated probe attempts for known-unsupported entities.
    /// </para>
    /// </remarks>
    public class BulkOperationProber
    {
        private readonly IBulkOperationExecutor _bulkExecutor;
        private readonly ILogger<BulkOperationProber>? _logger;

        /// <summary>
        /// Cache of entities that don't support bulk operations.
        /// Per-prober-instance scope - resets when prober is recreated.
        /// </summary>
        private readonly ConcurrentDictionary<string, bool> _bulkNotSupportedEntities = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="BulkOperationProber"/> class.
        /// </summary>
        /// <param name="bulkExecutor">The bulk operation executor.</param>
        /// <param name="logger">Optional logger.</param>
        public BulkOperationProber(
            IBulkOperationExecutor bulkExecutor,
            ILogger<BulkOperationProber>? logger = null)
        {
            _bulkExecutor = bulkExecutor ?? throw new ArgumentNullException(nameof(bulkExecutor));
            _logger = logger;
        }

        /// <summary>
        /// Gets whether an entity is known to not support bulk operations.
        /// </summary>
        /// <param name="entityName">The entity logical name.</param>
        /// <returns>True if the entity is known to not support bulk operations.</returns>
        public bool IsKnownBulkNotSupported(string entityName)
        {
            return _bulkNotSupportedEntities.ContainsKey(entityName);
        }

        /// <summary>
        /// Executes a bulk operation with probing to detect bulk support.
        /// </summary>
        /// <param name="entityName">The entity logical name.</param>
        /// <param name="records">The records to process.</param>
        /// <param name="operationType">The type of bulk operation.</param>
        /// <param name="options">Bulk operation options.</param>
        /// <param name="fallbackExecutor">Executor for individual operations when bulk isn't supported.</param>
        /// <param name="progress">Optional progress reporter.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The combined result from probe and remaining records.</returns>
        public async Task<BulkOperationResult> ExecuteWithProbeAsync(
            string entityName,
            IReadOnlyList<Entity> records,
            BulkOperationType operationType,
            BulkOperationOptions options,
            Func<string, IReadOnlyList<Entity>, Task<BulkOperationResult>> fallbackExecutor,
            IProgress<ProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            if (records == null || records.Count == 0)
            {
                return new BulkOperationResult
                {
                    SuccessCount = 0,
                    FailureCount = 0,
                    Errors = Array.Empty<BulkOperationError>()
                };
            }

            // If already known to not support bulk, go straight to fallback
            if (_bulkNotSupportedEntities.ContainsKey(entityName))
            {
                _logger?.LogDebug("Using fallback for {Entity} (bulk not supported)", entityName);
                return await fallbackExecutor(entityName, records).ConfigureAwait(false);
            }

            // Probe with first record
            var probeRecord = records.Take(1).ToList();
            var probeResult = await ExecuteBulkOperationAsync(
                entityName, probeRecord, operationType, options, null, cancellationToken).ConfigureAwait(false);

            if (IsBulkNotSupportedFailure(probeResult, 1))
            {
                // Cache that this entity doesn't support bulk operations
                _bulkNotSupportedEntities.TryAdd(entityName, true);
                _logger?.LogWarning("Entity {Entity} does not support bulk operations, falling back to individual operations", entityName);

                // Fall back to individual operations for ALL records (including the probe record)
                return await fallbackExecutor(entityName, records).ConfigureAwait(false);
            }

            // Probe succeeded - process remaining records in bulk
            if (records.Count > 1)
            {
                var remainingRecords = records.Skip(1).ToList();
                var remainingResult = await ExecuteBulkOperationAsync(
                    entityName, remainingRecords, operationType, options, progress, cancellationToken).ConfigureAwait(false);

                // Merge probe result with remaining result
                return MergeBulkResults(probeResult, remainingResult);
            }

            // Only had 1 record, probe was the entire batch
            return probeResult;
        }

        /// <summary>
        /// Marks an entity as not supporting bulk operations.
        /// </summary>
        /// <param name="entityName">The entity logical name.</param>
        /// <remarks>
        /// Call this when you detect bulk operation failure from an external source.
        /// </remarks>
        public void MarkBulkNotSupported(string entityName)
        {
            _bulkNotSupportedEntities.TryAdd(entityName, true);
        }

        private async Task<BulkOperationResult> ExecuteBulkOperationAsync(
            string entityName,
            IReadOnlyList<Entity> records,
            BulkOperationType operationType,
            BulkOperationOptions options,
            IProgress<ProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            return operationType switch
            {
                BulkOperationType.Create => await _bulkExecutor.CreateMultipleAsync(
                    entityName, records, options, progress, cancellationToken).ConfigureAwait(false),
                BulkOperationType.Update => await _bulkExecutor.UpdateMultipleAsync(
                    entityName, records, options, progress, cancellationToken).ConfigureAwait(false),
                BulkOperationType.Upsert => await _bulkExecutor.UpsertMultipleAsync(
                    entityName, records, options, progress, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(operationType), operationType, "Unknown bulk operation type")
            };
        }

        /// <summary>
        /// Determines if a bulk operation failure indicates the entity doesn't support bulk operations.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Some entities (like team, queue) don't support CreateMultiple/UpdateMultiple/UpsertMultiple.
        /// When detected, callers should fallback to individual operations.
        /// </para>
        /// <para>
        /// Error messages vary by entity:
        /// <list type="bullet">
        ///   <item>"is not enabled on the entity" (team)</item>
        ///   <item>"does not support entities of type" (queue)</item>
        /// </list>
        /// </para>
        /// </remarks>
        /// <param name="result">The bulk operation result.</param>
        /// <param name="totalRecords">The total number of records attempted.</param>
        /// <returns>True if the failure indicates bulk operations are not supported.</returns>
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

            return message.Contains("is not enabled on the entity", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("does not support entities of type", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Merges two bulk operation results into one combined result.
        /// Used when probe record is processed separately from remaining records.
        /// </summary>
        /// <param name="probeResult">The result from the probe operation (first record).</param>
        /// <param name="remainingResult">The result from processing remaining records.</param>
        /// <returns>A combined result with adjusted error indices.</returns>
        public static BulkOperationResult MergeBulkResults(BulkOperationResult probeResult, BulkOperationResult remainingResult)
        {
            // Adjust error indices in remaining result to account for probe record
            var adjustedErrors = remainingResult.Errors
                .Select(e => new BulkOperationError
                {
                    Index = e.Index + 1, // Offset by 1 for the probe record
                    RecordId = e.RecordId,
                    ErrorCode = e.ErrorCode,
                    Message = e.Message,
                    FieldName = e.FieldName,
                    FieldValueDescription = e.FieldValueDescription
                })
                .ToList();

            var allErrors = probeResult.Errors.Concat(adjustedErrors).ToList();

            return new BulkOperationResult
            {
                SuccessCount = probeResult.SuccessCount + remainingResult.SuccessCount,
                FailureCount = probeResult.FailureCount + remainingResult.FailureCount,
                CreatedCount = probeResult.CreatedCount + remainingResult.CreatedCount,
                UpdatedCount = probeResult.UpdatedCount + remainingResult.UpdatedCount,
                Errors = allErrors
            };
        }
    }

    /// <summary>
    /// The type of bulk operation to execute.
    /// </summary>
    public enum BulkOperationType
    {
        /// <summary>Create new records.</summary>
        Create,

        /// <summary>Update existing records.</summary>
        Update,

        /// <summary>Create or update records as needed.</summary>
        Upsert
    }
}
