using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
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
        private readonly ILogger<DeferredFieldProcessor>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeferredFieldProcessor"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="logger">Optional logger.</param>
        public DeferredFieldProcessor(
            IDataverseConnectionPool connectionPool,
            ILogger<DeferredFieldProcessor>? logger = null)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
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

            foreach (var (entityName, fields) in context.Plan.DeferredFields)
            {
                if (!context.Data.EntityData.TryGetValue(entityName, out var records))
                {
                    continue;
                }

                var fieldList = string.Join(", ", fields);
                var processed = 0;
                var updated = 0;

                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!context.IdMappings.TryGetNewId(entityName, record.Id, out var newId))
                    {
                        processed++;
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
                        await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);
                        await client.UpdateAsync(update).ConfigureAwait(false);
                        totalUpdated++;
                        updated++;
                    }

                    processed++;

                    // Report progress periodically (every 100 records or at completion)
                    if (processed % 100 == 0 || processed == records.Count)
                    {
                        context.Progress?.Report(new ProgressEventArgs
                        {
                            Phase = MigrationPhase.ProcessingDeferredFields,
                            Entity = entityName,
                            Field = fieldList,
                            Current = processed,
                            Total = records.Count,
                            SuccessCount = updated,
                            Message = $"Updating deferred fields: {fieldList}"
                        });
                    }
                }
            }

            stopwatch.Stop();
            _logger?.LogInformation("Updated {Count} deferred field records in {Duration}ms",
                totalUpdated, stopwatch.ElapsedMilliseconds);

            return new PhaseResult
            {
                Success = true,
                RecordsProcessed = totalUpdated,
                SuccessCount = totalUpdated,
                FailureCount = 0,
                Duration = stopwatch.Elapsed
            };
        }
    }
}
