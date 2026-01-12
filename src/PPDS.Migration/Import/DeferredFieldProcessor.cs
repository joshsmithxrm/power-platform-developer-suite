using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Security;
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
        private readonly BulkOperationProber _prober;
        private readonly ILogger<DeferredFieldProcessor>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeferredFieldProcessor"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="prober">The bulk operation prober.</param>
        /// <param name="logger">Optional logger.</param>
        public DeferredFieldProcessor(
            IDataverseConnectionPool connectionPool,
            BulkOperationProber prober,
            ILogger<DeferredFieldProcessor>? logger = null)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _prober = prober ?? throw new ArgumentNullException(nameof(prober));
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

                // Note: Final progress is already reported by ExecuteUpdatesAsync/ExecuteIndividualUpdatesAsync
                // Don't report again here to avoid duplicate completion lines
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

            // Create progress adapter
            var progressAdapter = new Progress<Dataverse.Progress.ProgressSnapshot>(snapshot =>
            {
                context.Progress?.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.ProcessingDeferredFields,
                    Entity = entityName,
                    Field = fieldList,
                    Current = (int)snapshot.Processed,
                    Total = updates.Count,
                    SuccessCount = (int)snapshot.Succeeded,
                    Message = $"Updating deferred fields: {fieldList}"
                });
            });

            var result = await _prober.ExecuteWithProbeAsync(
                entityName,
                updates,
                BulkOperationType.Update,
                bulkOptions,
                async (_, recs) => await ExecuteIndividualUpdatesAsync(entityName, recs.ToList(), fieldList, context, cancellationToken).ConfigureAwait(false),
                progressAdapter,
                cancellationToken).ConfigureAwait(false);

            return (result.SuccessCount, result.FailureCount);
        }

        private async Task<BulkOperationResult> ExecuteIndividualUpdatesAsync(
            string entityName,
            List<Entity> updates,
            string fieldList,
            ImportContext context,
            CancellationToken cancellationToken)
        {
            var successCount = 0;
            var failureCount = 0;
            var errors = new List<BulkOperationError>();

            // Use single client for sequential individual updates
            await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < updates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var updateRequest = new UpdateRequest { Target = updates[i] };
                    updateRequest.ApplyBypassOptions(context.Options);
                    await client.ExecuteAsync(updateRequest).ConfigureAwait(false);
                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to update deferred fields for {Entity} record {RecordId}",
                        entityName, updates[i].Id);
                    failureCount++;
                    errors.Add(new BulkOperationError
                    {
                        Index = i,
                        RecordId = updates[i].Id != Guid.Empty ? updates[i].Id : null,
                        ErrorCode = -1,
                        Message = ConnectionStringRedactor.RedactExceptionMessage(ex.Message)
                    });

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

            return new BulkOperationResult
            {
                SuccessCount = successCount,
                FailureCount = failureCount,
                Errors = errors
            };
        }
    }
}
