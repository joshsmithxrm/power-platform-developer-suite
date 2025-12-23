using System;
using System.Collections.Concurrent;
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
using PPDS.Migration.Analysis;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Tiered importer that respects dependency order.
    /// </summary>
    public class TieredImporter : IImporter
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly IBulkOperationExecutor _bulkExecutor;
        private readonly ICmtDataReader _dataReader;
        private readonly IDependencyGraphBuilder _graphBuilder;
        private readonly IExecutionPlanBuilder _planBuilder;
        private readonly IPluginStepManager? _pluginStepManager;
        private readonly ILogger<TieredImporter>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TieredImporter"/> class.
        /// </summary>
        public TieredImporter(
            IDataverseConnectionPool connectionPool,
            IBulkOperationExecutor bulkExecutor,
            ICmtDataReader dataReader,
            IDependencyGraphBuilder graphBuilder,
            IExecutionPlanBuilder planBuilder)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _bulkExecutor = bulkExecutor ?? throw new ArgumentNullException(nameof(bulkExecutor));
            _dataReader = dataReader ?? throw new ArgumentNullException(nameof(dataReader));
            _graphBuilder = graphBuilder ?? throw new ArgumentNullException(nameof(graphBuilder));
            _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TieredImporter"/> class.
        /// </summary>
        public TieredImporter(
            IDataverseConnectionPool connectionPool,
            IBulkOperationExecutor bulkExecutor,
            ICmtDataReader dataReader,
            IDependencyGraphBuilder graphBuilder,
            IExecutionPlanBuilder planBuilder,
            IPluginStepManager? pluginStepManager = null,
            ILogger<TieredImporter>? logger = null)
            : this(connectionPool, bulkExecutor, dataReader, graphBuilder, planBuilder)
        {
            _pluginStepManager = pluginStepManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<ImportResult> ImportAsync(
            string dataPath,
            ImportOptions? options = null,
            IProgressReporter? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = "Reading data archive..."
            });

            var data = await _dataReader.ReadAsync(dataPath, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = "Building dependency graph..."
            });

            var graph = _graphBuilder.Build(data.Schema);
            var plan = _planBuilder.Build(graph, data.Schema);

            return await ImportAsync(data, plan, options, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ImportResult> ImportAsync(
            MigrationData data,
            ExecutionPlan plan,
            ImportOptions? options = null,
            IProgressReporter? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            options ??= new ImportOptions();
            var stopwatch = Stopwatch.StartNew();
            var idMappings = new IdMappingCollection();
            var entityResults = new ConcurrentBag<EntityImportResult>();
            var errors = new ConcurrentBag<MigrationError>();
            var totalImported = 0;

            _logger?.LogInformation("Starting tiered import: {Tiers} tiers, {Records} records",
                plan.TierCount, data.TotalRecordCount);

            // Disable plugins on entities with disableplugins=true
            IReadOnlyList<Guid> disabledPluginSteps = Array.Empty<Guid>();
            if (options.RespectDisablePluginsSetting && _pluginStepManager != null)
            {
                var entitiesToDisablePlugins = data.Schema.Entities
                    .Where(e => e.DisablePlugins)
                    .Select(e => e.LogicalName)
                    .ToList();

                if (entitiesToDisablePlugins.Count > 0)
                {
                    progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Analyzing,
                        Message = $"Disabling plugins for {entitiesToDisablePlugins.Count} entities..."
                    });

                    disabledPluginSteps = await _pluginStepManager.GetActivePluginStepsAsync(
                        entitiesToDisablePlugins, cancellationToken).ConfigureAwait(false);

                    if (disabledPluginSteps.Count > 0)
                    {
                        await _pluginStepManager.DisablePluginStepsAsync(
                            disabledPluginSteps, cancellationToken).ConfigureAwait(false);

                        _logger?.LogInformation("Disabled {Count} plugin steps", disabledPluginSteps.Count);
                    }
                }
            }

            try
            {
                // Process each tier sequentially
                foreach (var tier in plan.Tiers)
                {
                    progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Importing,
                        TierNumber = tier.TierNumber,
                        Message = $"Processing tier {tier.TierNumber}: {string.Join(", ", tier.Entities)}"
                    });

                    // Process entities within tier in parallel
                    await Parallel.ForEachAsync(
                        tier.Entities,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = options.MaxParallelEntities,
                            CancellationToken = cancellationToken
                        },
                        async (entityName, ct) =>
                        {
                            if (!data.EntityData.TryGetValue(entityName, out var records) || records.Count == 0)
                            {
                                return;
                            }

                            // Get deferred fields for this entity
                            plan.DeferredFields.TryGetValue(entityName, out var deferredFields);

                            var result = await ImportEntityAsync(
                                entityName,
                                records,
                                tier.TierNumber,
                                deferredFields,
                                idMappings,
                                options,
                                progress,
                                ct).ConfigureAwait(false);

                            entityResults.Add(result);
                            Interlocked.Add(ref totalImported, result.SuccessCount);

                            if (!result.Success)
                            {
                                errors.Add(new MigrationError
                                {
                                    Phase = MigrationPhase.Importing,
                                    EntityLogicalName = entityName,
                                    Message = $"Entity import had {result.FailureCount} failures"
                                });
                            }
                        }).ConfigureAwait(false);

                    _logger?.LogInformation("Tier {Tier} complete", tier.TierNumber);
                }

                // Process deferred fields
                var deferredUpdates = 0;
                if (plan.DeferredFieldCount > 0)
                {
                    deferredUpdates = await ProcessDeferredFieldsAsync(
                        data, plan, idMappings, options, progress, cancellationToken).ConfigureAwait(false);
                }

                // Process M2M relationships
                var relationshipsProcessed = 0;
                if (data.RelationshipData.Count > 0)
                {
                    relationshipsProcessed = await ProcessRelationshipsAsync(
                        data, plan, idMappings, options, progress, cancellationToken).ConfigureAwait(false);
                }

                stopwatch.Stop();

                _logger?.LogInformation("Import complete: {Records} imported, {Deferred} deferred, {M2M} relationships in {Duration}",
                    totalImported, deferredUpdates, relationshipsProcessed, stopwatch.Elapsed);

                var result = new ImportResult
                {
                    Success = errors.IsEmpty,
                    TiersProcessed = plan.TierCount,
                    RecordsImported = totalImported,
                    RecordsUpdated = deferredUpdates,
                    RelationshipsProcessed = relationshipsProcessed,
                    Duration = stopwatch.Elapsed,
                    EntityResults = entityResults.ToArray(),
                    Errors = errors.ToArray()
                };

                // Calculate record-level failure count from entity results
                var recordFailureCount = entityResults.Sum(r => r.FailureCount);

                progress?.Complete(new MigrationResult
                {
                    Success = result.Success,
                    RecordsProcessed = result.RecordsImported + result.RecordsUpdated + recordFailureCount,
                    SuccessCount = result.RecordsImported + result.RecordsUpdated,
                    FailureCount = recordFailureCount,
                    Duration = result.Duration,
                    Errors = errors.ToArray()
                });

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                _logger?.LogError(ex, "Import failed");

                var safeMessage = ConnectionStringRedactor.RedactExceptionMessage(ex.Message);
                progress?.Error(ex, "Import failed");

                return new ImportResult
                {
                    Success = false,
                    TiersProcessed = plan.TierCount,
                    RecordsImported = totalImported,
                    Duration = stopwatch.Elapsed,
                    EntityResults = entityResults.ToArray(),
                    Errors = new[]
                    {
                        new MigrationError
                        {
                            Phase = MigrationPhase.Importing,
                            Message = safeMessage
                        }
                    }
                };
            }
            finally
            {
                // Re-enable plugins that were disabled
                if (disabledPluginSteps.Count > 0 && _pluginStepManager != null)
                {
                    progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Complete,
                        Message = $"Re-enabling {disabledPluginSteps.Count} plugin steps..."
                    });

                    try
                    {
                        await _pluginStepManager.EnablePluginStepsAsync(
                            disabledPluginSteps, CancellationToken.None).ConfigureAwait(false);

                        _logger?.LogInformation("Re-enabled {Count} plugin steps", disabledPluginSteps.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to re-enable some plugin steps");
                    }
                }
            }
        }

        private async Task<EntityImportResult> ImportEntityAsync(
            string entityName,
            IReadOnlyList<Entity> records,
            int tierNumber,
            IReadOnlyList<string>? deferredFields,
            IdMappingCollection idMappings,
            ImportOptions options,
            IProgressReporter? progress,
            CancellationToken cancellationToken)
        {
            var entityStopwatch = Stopwatch.StartNew();
            var successCount = 0;
            var failureCount = 0;
            var deferredSet = deferredFields != null
                ? new HashSet<string>(deferredFields, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _logger?.LogDebug("Importing {Count} records for {Entity}", records.Count, entityName);

            // Prepare records: remap lookups and null deferred fields
            var preparedRecords = new List<Entity>();
            foreach (var record in records)
            {
                var prepared = PrepareRecordForImport(record, deferredSet, idMappings, options);
                preparedRecords.Add(prepared);
            }

            // Batch import
            for (var i = 0; i < preparedRecords.Count; i += options.BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = preparedRecords.Skip(i).Take(options.BatchSize).ToList();
                var batchResult = await ImportBatchAsync(entityName, batch, options, cancellationToken).ConfigureAwait(false);

                // Track ID mappings
                for (var j = 0; j < batch.Count && j < batchResult.CreatedIds.Count; j++)
                {
                    var oldId = records[i + j].Id;
                    var newId = batchResult.CreatedIds[j];
                    idMappings.AddMapping(entityName, oldId, newId);
                }

                successCount += batchResult.SuccessCount;
                failureCount += batchResult.FailureCount;

                // Report progress
                var rps = entityStopwatch.Elapsed.TotalSeconds > 0
                    ? (successCount + failureCount) / entityStopwatch.Elapsed.TotalSeconds
                    : 0;

                progress?.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Importing,
                    Entity = entityName,
                    TierNumber = tierNumber,
                    Current = successCount + failureCount,
                    Total = records.Count,
                    SuccessCount = successCount,
                    FailureCount = failureCount,
                    RecordsPerSecond = rps
                });
            }

            entityStopwatch.Stop();

            return new EntityImportResult
            {
                EntityLogicalName = entityName,
                TierNumber = tierNumber,
                RecordCount = records.Count,
                SuccessCount = successCount,
                FailureCount = failureCount,
                Duration = entityStopwatch.Elapsed,
                Success = failureCount == 0
            };
        }

        private Entity PrepareRecordForImport(
            Entity record,
            HashSet<string> deferredFields,
            IdMappingCollection idMappings,
            ImportOptions options)
        {
            var prepared = new Entity(record.LogicalName);
            prepared.Id = record.Id; // Keep original ID for mapping

            // UpsertMultiple requires the primary key as an attribute, not just Entity.Id
            // Entity.Id is ignored during creation; must add as attribute for deterministic IDs
            if (record.Id != Guid.Empty)
            {
                var primaryKeyName = $"{record.LogicalName}id";
                prepared[primaryKeyName] = record.Id;
            }

            foreach (var attr in record.Attributes)
            {
                // Skip deferred fields
                if (deferredFields.Contains(attr.Key))
                {
                    continue;
                }

                // Remap entity references
                if (attr.Value is EntityReference er)
                {
                    var mappedRef = RemapEntityReference(er, idMappings, options);
                    if (mappedRef != null)
                    {
                        prepared[attr.Key] = mappedRef;
                    }
                    // If null, skip the field (can't be mapped)
                }
                else
                {
                    prepared[attr.Key] = attr.Value;
                }
            }

            return prepared;
        }

        private EntityReference? RemapEntityReference(
            EntityReference er,
            IdMappingCollection idMappings,
            ImportOptions options)
        {
            // Check if this is a user reference that should use user mapping
            if (IsUserReference(er.LogicalName) && options.UserMappings != null)
            {
                if (options.UserMappings.TryGetMappedUserId(er.Id, out var mappedUserId))
                {
                    return new EntityReference(er.LogicalName, mappedUserId);
                }
                // User mapping exists but no mapping found for this user
                // Return original if no default, otherwise the default would have been returned
                return new EntityReference(er.LogicalName, er.Id);
            }

            // Standard ID mapping for non-user references
            if (idMappings.TryGetNewId(er.LogicalName, er.Id, out var newId))
            {
                return new EntityReference(er.LogicalName, newId);
            }

            // Return original - will be processed in deferred phase if needed
            return new EntityReference(er.LogicalName, er.Id);
        }

        private static bool IsUserReference(string entityLogicalName)
        {
            return entityLogicalName.Equals("systemuser", StringComparison.OrdinalIgnoreCase) ||
                   entityLogicalName.Equals("team", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<BatchImportResult> ImportBatchAsync(
            string entityName,
            List<Entity> batch,
            ImportOptions options,
            CancellationToken cancellationToken)
        {
            var bulkOptions = new BulkOperationOptions
            {
                BatchSize = options.BatchSize,
                ContinueOnError = options.ContinueOnError,
                BypassCustomPluginExecution = options.BypassCustomPluginExecution
            };

            if (options.UseBulkApis)
            {
                var result = options.Mode switch
                {
                    ImportMode.Create => await _bulkExecutor.CreateMultipleAsync(entityName, batch, bulkOptions, progress: null, cancellationToken).ConfigureAwait(false),
                    ImportMode.Update => await _bulkExecutor.UpdateMultipleAsync(entityName, batch, bulkOptions, progress: null, cancellationToken).ConfigureAwait(false),
                    _ => await _bulkExecutor.UpsertMultipleAsync(entityName, batch, bulkOptions, progress: null, cancellationToken).ConfigureAwait(false)
                };

                return new BatchImportResult
                {
                    SuccessCount = result.SuccessCount,
                    FailureCount = result.FailureCount,
                    CreatedIds = batch.Select(e => e.Id).ToList() // For bulk, IDs are preserved
                };
            }
            else
            {
                // Fallback to individual operations
                var createdIds = new List<Guid>();
                var successCount = 0;
                var failureCount = 0;

                await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

                foreach (var record in batch)
                {
                    try
                    {
                        Guid newId;
                        switch (options.Mode)
                        {
                            case ImportMode.Create:
                                newId = await client.CreateAsync(record).ConfigureAwait(false);
                                break;
                            case ImportMode.Update:
                                await client.UpdateAsync(record).ConfigureAwait(false);
                                newId = record.Id;
                                break;
                            default:
                                var response = (UpsertResponse)await client.ExecuteAsync(new UpsertRequest { Target = record }).ConfigureAwait(false);
                                newId = response.Target?.Id ?? record.Id;
                                break;
                        }

                        createdIds.Add(newId);
                        successCount++;
                    }
                    catch
                    {
                        failureCount++;
                        if (!options.ContinueOnError)
                        {
                            throw;
                        }
                    }
                }

                return new BatchImportResult
                {
                    SuccessCount = successCount,
                    FailureCount = failureCount,
                    CreatedIds = createdIds
                };
            }
        }

        private async Task<int> ProcessDeferredFieldsAsync(
            MigrationData data,
            ExecutionPlan plan,
            IdMappingCollection idMappings,
            ImportOptions options,
            IProgressReporter? progress,
            CancellationToken cancellationToken)
        {
            var totalUpdated = 0;

            foreach (var (entityName, fields) in plan.DeferredFields)
            {
                if (!data.EntityData.TryGetValue(entityName, out var records))
                {
                    continue;
                }

                var fieldList = string.Join(", ", fields);
                var processed = 0;
                var updated = 0;

                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!idMappings.TryGetNewId(entityName, record.Id, out var newId))
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
                            if (idMappings.TryGetNewId(er.LogicalName, er.Id, out var mappedId))
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
                        progress?.Report(new ProgressEventArgs
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

            _logger?.LogInformation("Updated {Count} deferred field records", totalUpdated);
            return totalUpdated;
        }

        private async Task<int> ProcessRelationshipsAsync(
            MigrationData data,
            ExecutionPlan plan,
            IdMappingCollection idMappings,
            ImportOptions options,
            IProgressReporter? progress,
            CancellationToken cancellationToken)
        {
            var totalProcessed = 0;

            // Build role name-to-ID cache for role lookup
            Dictionary<string, Guid>? roleNameCache = null;

            foreach (var (entityName, m2mDataList) in data.RelationshipData)
            {
                foreach (var m2mData in m2mDataList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.ProcessingRelationships,
                        Entity = entityName,
                        Relationship = m2mData.RelationshipName,
                        Message = $"Processing {m2mData.RelationshipName}..."
                    });

                    // Get mapped source ID
                    if (!idMappings.TryGetNewId(entityName, m2mData.SourceId, out var sourceNewId))
                    {
                        _logger?.LogDebug("Skipping M2M for unmapped source {Entity}:{Id}",
                            entityName, m2mData.SourceId);
                        continue;
                    }

                    // Map target IDs - special handling for role entity
                    var mappedTargetIds = new List<Guid>();
                    var isRoleTarget = m2mData.TargetEntityName.Equals("role", StringComparison.OrdinalIgnoreCase);

                    foreach (var targetId in m2mData.TargetIds)
                    {
                        Guid? mappedId = null;

                        // First try direct ID mapping
                        if (idMappings.TryGetNewId(m2mData.TargetEntityName, targetId, out var directMappedId))
                        {
                            mappedId = directMappedId;
                        }
                        // For role entity, try lookup by name
                        else if (isRoleTarget)
                        {
                            roleNameCache ??= await BuildRoleNameCacheAsync(cancellationToken).ConfigureAwait(false);
                            mappedId = await LookupRoleByIdAsync(targetId, roleNameCache, cancellationToken).ConfigureAwait(false);
                        }

                        if (mappedId.HasValue)
                        {
                            mappedTargetIds.Add(mappedId.Value);
                        }
                        else
                        {
                            _logger?.LogDebug("Could not map target {Entity}:{Id} for relationship {Relationship}",
                                m2mData.TargetEntityName, targetId, m2mData.RelationshipName);
                        }
                    }

                    if (mappedTargetIds.Count == 0)
                    {
                        continue;
                    }

                    // Create association request
                    await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

                    var relatedEntities = new EntityReferenceCollection();
                    foreach (var targetId in mappedTargetIds)
                    {
                        relatedEntities.Add(new EntityReference(m2mData.TargetEntityName, targetId));
                    }

                    var request = new AssociateRequest
                    {
                        Target = new EntityReference(entityName, sourceNewId),
                        RelatedEntities = relatedEntities,
                        Relationship = new Relationship(m2mData.RelationshipName)
                    };

                    try
                    {
                        await client.ExecuteAsync(request).ConfigureAwait(false);
                        totalProcessed += mappedTargetIds.Count;
                    }
                    catch (Exception ex)
                    {
                        // M2M associations may fail if already exists - log but continue
                        _logger?.LogDebug(ex, "Failed to associate {Source} with {TargetCount} targets via {Relationship}",
                            sourceNewId, mappedTargetIds.Count, m2mData.RelationshipName);

                        if (!options.ContinueOnError)
                        {
                            throw;
                        }
                    }
                }
            }

            _logger?.LogInformation("Processed {Count} M2M associations", totalProcessed);
            return totalProcessed;
        }

        private async Task<Dictionary<string, Guid>> BuildRoleNameCacheAsync(CancellationToken cancellationToken)
        {
            var cache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            try
            {
                await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

                var fetchXml = @"<fetch>
                    <entity name='role'>
                        <attribute name='roleid' />
                        <attribute name='name' />
                    </entity>
                </fetch>";

                var response = await client.RetrieveMultipleAsync(
                    new Microsoft.Xrm.Sdk.Query.FetchExpression(fetchXml)).ConfigureAwait(false);

                foreach (var entity in response.Entities)
                {
                    var name = entity.GetAttributeValue<string>("name");
                    var id = entity.Id;
                    if (!string.IsNullOrEmpty(name) && !cache.ContainsKey(name))
                    {
                        cache[name] = id;
                    }
                }

                _logger?.LogDebug("Built role name cache with {Count} entries", cache.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to build role name cache");
            }

            return cache;
        }

        private async Task<Guid?> LookupRoleByIdAsync(
            Guid sourceRoleId,
            Dictionary<string, Guid> roleNameCache,
            CancellationToken cancellationToken)
        {
            // First, we need to get the role name from source environment
            // Since we only have the source ID, we need to query for it
            try
            {
                await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

                // Try to retrieve the role by ID - if it exists in target, we can use it directly
                var fetchXml = $@"<fetch top='1'>
                    <entity name='role'>
                        <attribute name='name' />
                        <filter>
                            <condition attribute='roleid' operator='eq' value='{sourceRoleId}' />
                        </filter>
                    </entity>
                </fetch>";

                var response = await client.RetrieveMultipleAsync(
                    new Microsoft.Xrm.Sdk.Query.FetchExpression(fetchXml)).ConfigureAwait(false);

                if (response.Entities.Count > 0)
                {
                    // Role exists with same ID in target
                    return sourceRoleId;
                }
            }
            catch
            {
                // Role doesn't exist with source ID, which is expected
            }

            // Role doesn't exist with source ID - this is the common case
            // We need to find it by name, but we don't have the source name here
            // For now, return null - proper solution requires exporting role names
            return null;
        }

        private class BatchImportResult
        {
            public int SuccessCount { get; set; }
            public int FailureCount { get; set; }
            public List<Guid> CreatedIds { get; set; } = new();
        }
    }
}
