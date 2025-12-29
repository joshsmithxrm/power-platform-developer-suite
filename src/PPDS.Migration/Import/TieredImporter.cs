using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Security;
using PPDS.Migration.Analysis;
using PPDS.Migration.DependencyInjection;
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
        private readonly ImportOptions _defaultOptions;
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
            _defaultOptions = new ImportOptions();
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
            IOptions<MigrationOptions>? migrationOptions = null,
            IPluginStepManager? pluginStepManager = null,
            ILogger<TieredImporter>? logger = null)
            : this(connectionPool, bulkExecutor, dataReader, graphBuilder, planBuilder)
        {
            _defaultOptions = migrationOptions?.Value.Import ?? new ImportOptions();
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

            options ??= _defaultOptions;
            var stopwatch = Stopwatch.StartNew();
            var idMappings = new IdMappingCollection();
            var entityResults = new ConcurrentBag<EntityImportResult>();
            var errors = new ConcurrentBag<MigrationError>();
            var totalImported = 0;

            _logger?.LogInformation("Starting tiered import: {Tiers} tiers, {Records} records",
                plan.TierCount, data.TotalRecordCount);

            // Load target environment field metadata for validity checking
            var entityNames = data.Schema.Entities.Select(e => e.LogicalName).ToList();
            var targetFieldMetadata = await LoadTargetFieldMetadataAsync(entityNames, progress, cancellationToken).ConfigureAwait(false);

            // Pre-flight check: detect columns that exist in export but not in target
            var missingColumns = DetectMissingColumns(data, targetFieldMetadata);
            if (missingColumns.Count > 0)
            {
                var totalMissing = missingColumns.Values.Sum(v => v.Count);

                if (!options.SkipMissingColumns)
                {
                    // Build detailed error message
                    var details = new System.Text.StringBuilder();
                    details.AppendLine($"Schema mismatch: {totalMissing} column(s) in exported data do not exist in target environment.");
                    details.AppendLine();

                    foreach (var (entity, columns) in missingColumns.OrderBy(x => x.Key))
                    {
                        details.AppendLine($"  {entity}:");
                        foreach (var col in columns)
                        {
                            details.AppendLine($"    - {col}");
                        }
                    }

                    details.AppendLine();
                    details.Append("Use --skip-missing-columns to import anyway (these columns will be skipped).");

                    _logger?.LogError("Schema mismatch detected: {Count} columns missing in target", totalMissing);

                    progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Analyzing,
                        Message = $"Schema mismatch: {totalMissing} column(s) not found in target"
                    });

                    throw new SchemaMismatchException(details.ToString(), missingColumns);
                }

                // SkipMissingColumns is true - log warnings and continue
                _logger?.LogWarning("Skipping {Count} columns not found in target environment", totalMissing);

                foreach (var (entity, columns) in missingColumns)
                {
                    _logger?.LogWarning("Entity {Entity}: skipping columns [{Columns}]",
                        entity, string.Join(", ", columns));
                }

                progress?.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = $"Warning: Skipping {totalMissing} column(s) not found in target"
                });
            }

            // Disable plugins on entities with disableplugins=true
            IReadOnlyList<Guid> disabledPluginSteps = Array.Empty<Guid>();
            if (options.RespectDisablePluginsSetting && _pluginStepManager != null)
            {
                var entitiesToDisablePlugins = data.Schema.Entities
                    .Where(e => e.DisablePlugins && e.ObjectTypeCode.HasValue)
                    .Select(e => e.ObjectTypeCode!.Value)
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

                            // Get field metadata for this entity
                            targetFieldMetadata.TryGetValue(entityName, out var entityFieldMetadata);

                            var result = await ImportEntityAsync(
                                entityName,
                                records,
                                tier.TierNumber,
                                deferredFields,
                                entityFieldMetadata,
                                idMappings,
                                options,
                                progress,
                                ct).ConfigureAwait(false);

                            entityResults.Add(result);
                            Interlocked.Add(ref totalImported, result.SuccessCount);

                            // Add all detailed errors from this entity
                            foreach (var error in result.Errors)
                            {
                                errors.Add(error);
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

                // Aggregate created/updated counts from entity results (only populated for upsert mode)
                var totalCreated = entityResults.Any(r => r.CreatedCount.HasValue)
                    ? entityResults.Sum(r => r.CreatedCount ?? 0)
                    : (int?)null;
                var totalUpdated = entityResults.Any(r => r.UpdatedCount.HasValue)
                    ? entityResults.Sum(r => r.UpdatedCount ?? 0)
                    : (int?)null;

                progress?.Complete(new MigrationResult
                {
                    Success = result.Success,
                    RecordsProcessed = result.RecordsImported + result.RecordsUpdated + recordFailureCount,
                    SuccessCount = result.RecordsImported + result.RecordsUpdated,
                    FailureCount = recordFailureCount,
                    CreatedCount = totalCreated,
                    UpdatedCount = totalUpdated,
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
            Dictionary<string, (bool IsValidForCreate, bool IsValidForUpdate)>? fieldMetadata,
            IdMappingCollection idMappings,
            ImportOptions options,
            IProgressReporter? progress,
            CancellationToken cancellationToken)
        {
            var entityStopwatch = Stopwatch.StartNew();
            var deferredSet = deferredFields != null
                ? new HashSet<string>(deferredFields, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _logger?.LogDebug("Importing {Count} records for {Entity}", records.Count, entityName);

            // Prepare records: remap lookups, null deferred fields, and filter based on operation validity
            var preparedRecords = new List<Entity>();
            foreach (var record in records)
            {
                var prepared = PrepareRecordForImport(record, deferredSet, fieldMetadata, idMappings, options);
                preparedRecords.Add(prepared);
            }

            // Create progress adapter that bridges BulkOperationExecutor progress to IProgressReporter
            var progressAdapter = progress != null
                ? new Progress<Dataverse.Progress.ProgressSnapshot>(snapshot =>
                {
                    progress.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Importing,
                        Entity = entityName,
                        TierNumber = tierNumber,
                        Current = (int)snapshot.Processed,
                        Total = (int)snapshot.Total,
                        SuccessCount = (int)snapshot.Succeeded,
                        FailureCount = (int)snapshot.Failed,
                        RecordsPerSecond = snapshot.RatePerSecond,
                        EstimatedRemaining = snapshot.EstimatedRemaining
                    });
                })
                : null;

            // Pass ALL records to BulkOperationExecutor - it handles batching dynamically
            var bulkOptions = new BulkOperationOptions
            {
                ContinueOnError = options.ContinueOnError,
                BypassCustomLogic = options.BypassCustomPlugins,
                BypassPowerAutomateFlows = options.BypassPowerAutomateFlows
            };

            BulkOperationResult bulkResult;
            if (options.UseBulkApis)
            {
                bulkResult = options.Mode switch
                {
                    ImportMode.Create => await _bulkExecutor.CreateMultipleAsync(entityName, preparedRecords, bulkOptions, progressAdapter, cancellationToken).ConfigureAwait(false),
                    ImportMode.Update => await _bulkExecutor.UpdateMultipleAsync(entityName, preparedRecords, bulkOptions, progressAdapter, cancellationToken).ConfigureAwait(false),
                    _ => await _bulkExecutor.UpsertMultipleAsync(entityName, preparedRecords, bulkOptions, progressAdapter, cancellationToken).ConfigureAwait(false)
                };
            }
            else
            {
                // Fallback to individual operations
                bulkResult = await ExecuteIndividualOperationsAsync(entityName, preparedRecords, options, cancellationToken).ConfigureAwait(false);
            }

            // Track ID mappings - for bulk operations, IDs are preserved from Entity.Id
            for (var i = 0; i < records.Count; i++)
            {
                var oldId = records[i].Id;
                idMappings.AddMapping(entityName, oldId, oldId);
            }

            // Convert BulkOperationErrors to MigrationErrors
            var allErrors = bulkResult.Errors.Select(e => new MigrationError
            {
                Phase = MigrationPhase.Importing,
                EntityLogicalName = entityName,
                RecordIndex = e.Index,
                ErrorCode = e.ErrorCode,
                Message = e.Message
            }).ToList();

            entityStopwatch.Stop();

            return new EntityImportResult
            {
                EntityLogicalName = entityName,
                TierNumber = tierNumber,
                RecordCount = records.Count,
                SuccessCount = bulkResult.SuccessCount,
                FailureCount = bulkResult.FailureCount,
                CreatedCount = bulkResult.CreatedCount,
                UpdatedCount = bulkResult.UpdatedCount,
                Duration = entityStopwatch.Elapsed,
                Success = bulkResult.FailureCount == 0,
                Errors = allErrors
            };
        }

        private async Task<BulkOperationResult> ExecuteIndividualOperationsAsync(
            string entityName,
            List<Entity> records,
            ImportOptions options,
            CancellationToken cancellationToken)
        {
            var createdIds = new List<Guid>();
            var errors = new List<BulkOperationError>();
            var successCount = 0;
            var failureCount = 0;

            await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
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
                catch (Exception ex)
                {
                    failureCount++;
                    errors.Add(new BulkOperationError
                    {
                        Index = i,
                        RecordId = record.Id != Guid.Empty ? record.Id : null,
                        ErrorCode = -1,
                        Message = ex.Message
                    });

                    if (!options.ContinueOnError)
                    {
                        throw;
                    }
                }
            }

            return new BulkOperationResult
            {
                SuccessCount = successCount,
                FailureCount = failureCount,
                CreatedIds = createdIds,
                Errors = errors,
                Duration = TimeSpan.Zero
            };
        }

        private Entity PrepareRecordForImport(
            Entity record,
            HashSet<string> deferredFields,
            Dictionary<string, (bool IsValidForCreate, bool IsValidForUpdate)>? fieldMetadata,
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

                // Skip owner fields if stripping is enabled
                if (options.StripOwnerFields && IsOwnerField(attr.Key))
                {
                    continue;
                }

                // Skip fields that are not valid for the current operation based on target metadata
                if (!ShouldIncludeField(attr.Key, options.Mode, fieldMetadata, out _))
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

        /// <summary>
        /// Determines if a field is an owner-related field that should be stripped
        /// when importing to a different environment.
        /// </summary>
        private static bool IsOwnerField(string fieldName)
        {
            return fieldName.Equals("ownerid", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("createdby", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("modifiedby", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("createdonbehalfby", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("modifiedonbehalfby", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("owninguser", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("owningteam", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("owningbusinessunit", StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Loads field validity metadata from the target environment for all entities.
        /// This is used to determine which fields are valid for create/update operations.
        /// </summary>
        private async Task<Dictionary<string, Dictionary<string, (bool IsValidForCreate, bool IsValidForUpdate)>>> LoadTargetFieldMetadataAsync(
            IEnumerable<string> entityNames,
            IProgressReporter? progress,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, Dictionary<string, (bool, bool)>>(StringComparer.OrdinalIgnoreCase);

            progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = "Loading target environment field metadata..."
            });

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var entityName in entityNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var request = new RetrieveEntityRequest
                    {
                        LogicalName = entityName,
                        EntityFilters = EntityFilters.Attributes
                    };

                    var response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

                    var attrValidity = new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);
                    if (response.EntityMetadata.Attributes != null)
                    {
                        foreach (var attr in response.EntityMetadata.Attributes)
                        {
                            attrValidity[attr.LogicalName] = (
                                attr.IsValidForCreate ?? false,
                                attr.IsValidForUpdate ?? false
                            );
                        }
                    }

                    result[entityName] = attrValidity;
                    _logger?.LogDebug("Loaded metadata for {Entity}: {Count} attributes", entityName, attrValidity.Count);
                }
                catch (FaultException ex)
                {
                    _logger?.LogWarning(ex, "Failed to load metadata for entity {Entity}, using schema defaults", entityName);
                    // Entity might not exist in target - use empty metadata (will use schema defaults)
                    result[entityName] = new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);
                }
            }

            _logger?.LogInformation("Loaded field metadata for {Count} entities", result.Count);
            return result;
        }

        /// <summary>
        /// Determines if a field should be included in the import based on operation mode and metadata.
        /// </summary>
        /// <param name="fieldName">The field name to check.</param>
        /// <param name="mode">The import mode.</param>
        /// <param name="fieldMetadata">Target environment field metadata.</param>
        /// <param name="reason">Output: reason field was excluded, if any.</param>
        /// <returns>True if field should be included, false otherwise.</returns>
        private static bool ShouldIncludeField(
            string fieldName,
            ImportMode mode,
            Dictionary<string, (bool IsValidForCreate, bool IsValidForUpdate)>? fieldMetadata,
            out string? reason)
        {
            reason = null;

            // If no metadata available for this entity, skip unknown fields to prevent Dataverse errors
            if (fieldMetadata == null || !fieldMetadata.TryGetValue(fieldName, out var validity))
            {
                reason = "not found in target";
                return false;
            }

            var (isValidForCreate, isValidForUpdate) = validity;

            // Never include fields that are not valid for any write operation
            if (!isValidForCreate && !isValidForUpdate)
            {
                reason = "not valid for create or update";
                return false;
            }

            // For Update mode, skip fields not valid for update
            if (mode == ImportMode.Update && !isValidForUpdate)
            {
                reason = "not valid for update";
                return false;
            }

            // For Create mode, skip fields not valid for create
            if (mode == ImportMode.Create && !isValidForCreate)
            {
                reason = "not valid for create";
                return false;
            }

            // For Upsert mode, include fields valid for either operation
            // (the actual operation will determine validity per-record)
            return true;
        }

        /// <summary>
        /// Detects columns in exported data that don't exist in target environment.
        /// </summary>
        /// <returns>Dictionary of entity name to list of missing column names.</returns>
        private static Dictionary<string, List<string>> DetectMissingColumns(
            MigrationData data,
            Dictionary<string, Dictionary<string, (bool IsValidForCreate, bool IsValidForUpdate)>> targetFieldMetadata)
        {
            var missingColumns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (entityName, records) in data.EntityData)
            {
                if (records.Count == 0)
                    continue;

                // Get all unique field names from exported records
                var exportedFields = records
                    .SelectMany(r => r.Attributes.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Get target metadata for this entity
                targetFieldMetadata.TryGetValue(entityName, out var targetFields);
                targetFields ??= new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);

                // Find fields that exist in export but not in target
                var missing = exportedFields
                    .Where(f => !targetFields.ContainsKey(f))
                    .Where(f => !f.EndsWith("id", StringComparison.OrdinalIgnoreCase) ||
                                !f.Equals($"{entityName}id", StringComparison.OrdinalIgnoreCase)) // Skip primary key
                    .OrderBy(f => f)
                    .ToList();

                if (missing.Count > 0)
                {
                    missingColumns[entityName] = missing;
                }
            }

            return missingColumns;
        }
    }
}
