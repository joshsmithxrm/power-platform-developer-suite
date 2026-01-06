using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Processes deferred fields after the initial entity import.
    /// Deferred fields are self-referential lookups that couldn't be set during initial import
    /// because the target records didn't exist yet.
    /// </summary>
    public class DeferredFieldProcessor : IImportPhaseProcessor
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly IBulkOperationExecutor _bulkExecutor;
        private readonly ILogger<DeferredFieldProcessor>? _logger;

        /// <summary>
        /// Cache of entities that don't support bulk update operations.
        /// Per-session scope - resets each import.
        /// </summary>
        private readonly ConcurrentDictionary<string, bool> _bulkNotSupportedEntities = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="DeferredFieldProcessor"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="bulkExecutor">The bulk operation executor.</param>
        /// <param name="logger">Optional logger.</param>
        public DeferredFieldProcessor(
            IDataverseConnectionPool connectionPool,
            IBulkOperationExecutor bulkExecutor,
            ILogger<DeferredFieldProcessor>? logger = null)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _bulkExecutor = bulkExecutor ?? throw new ArgumentNullException(nameof(bulkExecutor));
            _logger = logger;
        }

        /// <inheritdoc />
        public string PhaseName => "Deferred Fields";

        /// <inheritdoc />
        public async Task<PhaseResult> ProcessAsync(
            ImportContext context,
            CancellationToken cancellationToken)
        {
            if (context.Plan.DeferredFieldCount == 0)
            {
                _logger?.LogDebug("No deferred fields to process");
                return PhaseResult.Skipped();
            }

            var stopwatch = Stopwatch.StartNew();
            var totalUpdated = 0;
            var totalFailures = 0;

            foreach (var (entityName, fields) in context.Plan.DeferredFields)
            {
                if (!context.Data.EntityData.TryGetValue(entityName, out var records))
                {
                    continue;
                }

                var fieldList = string.Join(", ", fields);

                // Collect all updates for this entity upfront
                var updates = new List<Entity>();
                foreach (var record in records)
                {
                    if (!context.IdMappings.TryGetNewId(entityName, record.Id, out var newId))
                    {
                        continue;
                    }

                    var update = new Entity(entityName, newId);
                    var hasUpdates = false;

                    foreach (var fieldName in fields)
                    {
                        if (record.Contains(fieldName) && record[fieldName] is EntityReference er)
                        {
                            if (context.IdMappings.TryGetNewId(er.LogicalName, er.Id, out var mappedId))
                            {
                                update[fieldName] = new EntityReference(er.LogicalName, mappedId);
                                hasUpdates = true;
                            }
                        }
                    }

                    if (hasUpdates)
                    {
                        updates.Add(update);
                    }
                }

                if (updates.Count == 0)
                {
                    _logger?.LogDebug("No deferred field updates needed for {Entity}", entityName);
                    continue;
                }

                _logger?.LogDebug("Processing {Count} deferred field updates for {Entity} ({Fields})",
                    updates.Count, entityName, fieldList);

                // Report initial progress
                context.Progress?.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.ProcessingDeferredFields,
                    Entity = entityName,
                    Field = fieldList,
                    Current = 0,
                    Total = updates.Count,
                    Message = $"Updating deferred fields: {fieldList}"
                });

                // Execute updates (bulk or individual based on entity support)
                var (successCount, failureCount) = await ExecuteUpdatesAsync(
                    entityName, updates, fieldList, context, cancellationToken).ConfigureAwait(false);

                totalUpdated += successCount;
                totalFailures += failureCount;

                // Report completion for this entity
                context.Progress?.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.ProcessingDeferredFields,
                    Entity = entityName,
                    Field = fieldList,
                    Current = updates.Count,
                    Total = updates.Count,
                    SuccessCount = successCount,
                    FailureCount = failureCount,
                    Message = $"[Deferred] {entityName}: {fieldList}"
                });
            }

            stopwatch.Stop();
            _logger?.LogInformation("Updated {Count} deferred field records in {Duration}ms",
                totalUpdated, stopwatch.ElapsedMilliseconds);

            return new PhaseResult
            {
                Success = totalFailures == 0 || context.Options.ContinueOnError,
                RecordsProcessed = totalUpdated + totalFailures,
                SuccessCount = totalUpdated,
                FailureCount = totalFailures,
                Duration = stopwatch.Elapsed
            };
        }

        private async Task<(int SuccessCount, int FailureCount)> ExecuteUpdatesAsync(
            string entityName,
            List<Entity> updates,
            string fieldList,
            ImportContext context,
            CancellationToken cancellationToken)
        {
            var bulkOptions = new BulkOperationOptions
            {
                ContinueOnError = context.Options.ContinueOnError,
                BypassCustomLogic = context.Options.BypassCustomPlugins,
                BypassPowerAutomateFlows = context.Options.BypassPowerAutomateFlows
            };

            // Check if we already know this entity doesn't support bulk operations
            if (_bulkNotSupportedEntities.ContainsKey(entityName))
            {
                _logger?.LogDebug("Using individual operations for {Entity} (bulk not supported)", entityName);
                return await ExecuteIndividualUpdatesAsync(entityName, updates, fieldList, context, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Probe with first record to detect bulk operation support
            var probeRecord = new List<Entity> { updates[0] };
            var probeResult = await _bulkExecutor.UpdateMultipleAsync(entityName, probeRecord, bulkOptions, null, cancellationToken)
                .ConfigureAwait(false);

            if (IsBulkNotSupportedFailure(probeResult, 1))
            {
                // Cache that this entity doesn't support bulk operations
                _bulkNotSupportedEntities[entityName] = true;
                _logger?.LogWarning("Entity {Entity} does not support bulk UpdateMultiple, falling back to individual operations", entityName);

                // Fall back to individual operations for ALL records
                return await ExecuteIndividualUpdatesAsync(entityName, updates, fieldList, context, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Probe succeeded - process remaining records in bulk
            var successCount = probeResult.SuccessCount;
            var failureCount = probeResult.FailureCount;

            if (updates.Count > 1)
            {
                var remainingRecords = updates.GetRange(1, updates.Count - 1);

                // Create progress adapter for remaining records
                var processedCount = 1; // Already processed probe record
                var progressAdapter = new Progress<Dataverse.Progress.ProgressSnapshot>(snapshot =>
                {
                    context.Progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.ProcessingDeferredFields,
                        Entity = entityName,
                        Field = fieldList,
                        Current = processedCount + (int)snapshot.Processed,
                        Total = updates.Count,
                        SuccessCount = successCount + (int)snapshot.Succeeded,
                        Message = $"Updating deferred fields: {fieldList}"
                    });
                });

                var remainingResult = await _bulkExecutor.UpdateMultipleAsync(
                    entityName, remainingRecords, bulkOptions, progressAdapter, cancellationToken).ConfigureAwait(false);

                successCount += remainingResult.SuccessCount;
                failureCount += remainingResult.FailureCount;
            }

            return (successCount, failureCount);
        }

        private async Task<(int SuccessCount, int FailureCount)> ExecuteIndividualUpdatesAsync(
            string entityName,
            List<Entity> updates,
            string fieldList,
            ImportContext context,
            CancellationToken cancellationToken)
        {
            var successCount = 0;
            var failureCount = 0;

            // Use single client for sequential individual updates
            await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < updates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await client.UpdateAsync(updates[i]).ConfigureAwait(false);
                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to update deferred fields for {Entity} record {RecordId}",
                        entityName, updates[i].Id);
                    failureCount++;

                    if (!context.Options.ContinueOnError)
                    {
                        throw;
                    }
                }

                // Report progress periodically (every 100 records)
                if ((i + 1) % 100 == 0 || i == updates.Count - 1)
                {
                    context.Progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.ProcessingDeferredFields,
                        Entity = entityName,
                        Field = fieldList,
                        Current = i + 1,
                        Total = updates.Count,
                        SuccessCount = successCount,
                        FailureCount = failureCount,
                        Message = $"Updating deferred fields: {fieldList}"
                    });
                }
            }

            return (successCount, failureCount);
        }

        /// <summary>
        /// Determines if a bulk operation failure indicates the entity doesn't support bulk operations.
        /// </summary>
        private static bool IsBulkNotSupportedFailure(BulkOperationResult result, int totalRecords)
        {
            // Only consider it a "not supported" failure if ALL records failed
            if (result.FailureCount != totalRecords || result.Errors.Count == 0)
                return false;

            // Check if first error indicates bulk operation not supported
            var firstError = result.Errors[0];
            return firstError.Message?.Contains("is not enabled on the entity", StringComparison.OrdinalIgnoreCase) ?? false;
        }
    }
}
