using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
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
    /// Orchestrates the import pipeline: entity import, deferred fields, and relationships.
    /// </summary>
    public class TieredImporter : IImporter
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly IBulkOperationExecutor _bulkExecutor;
        private readonly ICmtDataReader _dataReader;
        private readonly IDependencyGraphBuilder _graphBuilder;
        private readonly IExecutionPlanBuilder _planBuilder;
        private readonly ISchemaValidator _schemaValidator;
        private readonly DeferredFieldProcessor _deferredFieldProcessor;
        private readonly RelationshipProcessor _relationshipProcessor;
        private readonly BulkOperationProber _prober;
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
            IExecutionPlanBuilder planBuilder,
            ISchemaValidator schemaValidator,
            DeferredFieldProcessor deferredFieldProcessor,
            RelationshipProcessor relationshipProcessor,
            BulkOperationProber prober)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _bulkExecutor = bulkExecutor ?? throw new ArgumentNullException(nameof(bulkExecutor));
            _dataReader = dataReader ?? throw new ArgumentNullException(nameof(dataReader));
            _graphBuilder = graphBuilder ?? throw new ArgumentNullException(nameof(graphBuilder));
            _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
            _schemaValidator = schemaValidator ?? throw new ArgumentNullException(nameof(schemaValidator));
            _deferredFieldProcessor = deferredFieldProcessor ?? throw new ArgumentNullException(nameof(deferredFieldProcessor));
            _relationshipProcessor = relationshipProcessor ?? throw new ArgumentNullException(nameof(relationshipProcessor));
            _prober = prober ?? throw new ArgumentNullException(nameof(prober));
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
            ISchemaValidator schemaValidator,
            DeferredFieldProcessor deferredFieldProcessor,
            RelationshipProcessor relationshipProcessor,
            BulkOperationProber prober,
            IOptions<MigrationOptions>? migrationOptions = null,
            IPluginStepManager? pluginStepManager = null,
            ILogger<TieredImporter>? logger = null)
            : this(connectionPool, bulkExecutor, dataReader, graphBuilder, planBuilder,
                   schemaValidator, deferredFieldProcessor, relationshipProcessor, prober)
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
            var warnings = new WarningCollector();
            var totalImported = 0;

            // Track overall progress across all entities
            var totalEntities = data.EntityData.Count;
            var overallTotal = data.TotalRecordCount;
            long overallProcessed = 0;
            var entityIndex = 0;

            _logger?.LogInformation("Starting tiered import: {Tiers} tiers, {Records} records",
                plan.TierCount, data.TotalRecordCount);

            // Load target environment field metadata for validity checking
            var entityNames = data.Schema.Entities.Select(e => e.LogicalName).ToList();
            var targetFieldMetadata = await _schemaValidator.LoadTargetFieldMetadataAsync(
                entityNames, progress, cancellationToken).ConfigureAwait(false);

            // Pre-flight check: detect columns that exist in export but not in target
            var mismatchResult = _schemaValidator.DetectMissingColumns(data, targetFieldMetadata);
            if (mismatchResult.HasMissingColumns)
            {
                if (!options.SkipMissingColumns)
                {
                    _logger?.LogError("Schema mismatch detected: {Count} columns missing in target",
                        mismatchResult.TotalMissingCount);

                    progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Analyzing,
                        Message = $"Schema mismatch: {mismatchResult.TotalMissingCount} column(s) not found in target"
                    });

                    throw new SchemaMismatchException(
                        mismatchResult.BuildDetailedMessage(),
                        mismatchResult.MissingColumns.ToDictionary(x => x.Key, x => x.Value));
                }

                // SkipMissingColumns is true - log warnings and continue
                _logger?.LogWarning("Skipping {Count} columns not found in target environment",
                    mismatchResult.TotalMissingCount);

                foreach (var (entity, columns) in mismatchResult.MissingColumns)
                {
                    _logger?.LogWarning("Entity {Entity}: skipping columns [{Columns}]",
                        entity, string.Join(", ", columns));

                    warnings.AddWarning(new ImportWarning
                    {
                        Code = ImportWarningCodes.ColumnSkipped,
                        Entity = entity,
                        Message = $"Skipping columns not found in target: {string.Join(", ", columns)}",
                        Impact = $"{columns.Count} column(s) skipped"
                    });
                }

                progress?.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = $"Warning: Skipping {mismatchResult.TotalMissingCount} column(s) not found in target"
                });
            }

            // Create shared import context for phase processors
            var context = new ImportContext(data, plan, options, idMappings, targetFieldMetadata, progress)
            {
                OutputManager = options.OutputManager
            };

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
                // Phase 1: Process each tier sequentially
                foreach (var tier in plan.Tiers)
                {
                    // Track tier-level statistics
                    var tierStopwatch = Stopwatch.StartNew();
                    var tierRecordsProcessed = 0L;
                    var tierRecordsSuccess = 0L;
                    var tierRecordsFailed = 0L;

                    // Log tier start checkpoint
                    context.OutputManager?.LogTierStart(tier.TierNumber, tier.Entities.ToArray());

                    progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Importing,
                        TierNumber = tier.TierNumber,
                        TotalTiers = plan.TierCount,
                        TotalEntities = totalEntities,
                        OverallProcessed = Interlocked.Read(ref overallProcessed),
                        OverallTotal = overallTotal,
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
                            var entityFieldMetadata = targetFieldMetadata.GetFieldsForEntity(entityName);

                            var result = await ImportEntityAsync(
                                entityName,
                                records,
                                tier.TierNumber,
                                deferredFields,
                                entityFieldMetadata,
                                idMappings,
                                options,
                                progress,
                                warnings,
                                ct).ConfigureAwait(false);

                            entityResults.Add(result);
                            Interlocked.Add(ref totalImported, result.SuccessCount);

                            // Update tier-level tracking
                            Interlocked.Add(ref tierRecordsProcessed, result.RecordCount);
                            Interlocked.Add(ref tierRecordsSuccess, result.SuccessCount);
                            Interlocked.Add(ref tierRecordsFailed, result.FailureCount);

                            // Update overall progress tracking
                            Interlocked.Add(ref overallProcessed, result.RecordCount);
                            Interlocked.Increment(ref entityIndex);

                            // Log entity completion checkpoint
                            if (result.Duration.TotalSeconds > 0)
                            {
                                var rps = result.SuccessCount / result.Duration.TotalSeconds;
                                context.OutputManager?.LogEntityProgress(entityName, result.SuccessCount, records.Count, rps);
                            }

                            // Log entity error checkpoint if failures occurred
                            if (result.FailureCount > 0)
                            {
                                var errorSummary = result.Errors.FirstOrDefault()?.Message ?? "Multiple errors";
                                context.OutputManager?.LogEntityError(entityName, result.FailureCount, errorSummary);
                            }

                            // Add all detailed errors from this entity
                            foreach (var error in result.Errors)
                            {
                                errors.Add(error);

                                // Stream error immediately via callback (thread-safe)
                                options.ErrorCallback?.Invoke(error);
                            }
                        }).ConfigureAwait(false);

                    // Report tier completion summary
                    tierStopwatch.Stop();
                    var tierDuration = tierStopwatch.Elapsed;
                    var tierRps = tierDuration.TotalSeconds > 0
                        ? tierRecordsSuccess / tierDuration.TotalSeconds
                        : 0;

                    var tierSummaryMessage = tierRecordsFailed > 0
                        ? $"Tier {tier.TierNumber} completed: {tier.Entities.Count()} entities, {tierRecordsSuccess:N0} records ({tierRecordsFailed:N0} failed) in {tierDuration:mm\\:ss} @ {tierRps:F0} rec/s"
                        : $"Tier {tier.TierNumber} completed: {tier.Entities.Count()} entities, {tierRecordsSuccess:N0} records in {tierDuration:mm\\:ss} @ {tierRps:F0} rec/s";

                    progress?.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Importing,
                        TierNumber = tier.TierNumber,
                        TotalTiers = plan.TierCount,
                        Message = tierSummaryMessage
                    });

                    context.OutputManager?.LogProgress(tierSummaryMessage);

                    _logger?.LogInformation("Tier {Tier} complete: {Entities} entities, {Records} records in {Duration}",
                        tier.TierNumber, tier.Entities.Count(), tierRecordsSuccess, tierDuration);
                }

                // Capture Phase 1 duration (entity import)
                var phase1Duration = stopwatch.Elapsed;

                // Phase 2: Process deferred fields
                context.OutputManager?.LogProgress("Starting deferred fields phase");
                var deferredResult = await _deferredFieldProcessor.ProcessAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                var deferredUpdates = deferredResult.SuccessCount;
                var phase2Duration = deferredResult.Duration;

                // Phase 3: Process M2M relationships
                context.OutputManager?.LogProgress("Starting M2M relationships phase");
                var relationshipResult = await _relationshipProcessor.ProcessAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                var relationshipsProcessed = relationshipResult.SuccessCount;
                var phase3Duration = relationshipResult.Duration;

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
                    Phase1Duration = phase1Duration,
                    Phase2Duration = phase2Duration,
                    Phase3Duration = phase3Duration,
                    EntityResults = entityResults.ToArray(),
                    Errors = errors.ToArray(),
                    PoolStatistics = _connectionPool.Statistics,
                    Warnings = warnings.GetWarnings()
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

                // Include M2M relationship failures in total failure count
                var m2mFailureCount = relationshipResult.FailureCount;
                var totalFailureCount = recordFailureCount + m2mFailureCount;

                progress?.Complete(new MigrationResult
                {
                    Success = result.Success,
                    RecordsProcessed = result.RecordsImported + result.RecordsUpdated + totalFailureCount + relationshipsProcessed,
                    SuccessCount = result.RecordsImported + result.RecordsUpdated + relationshipsProcessed,
                    FailureCount = totalFailureCount,
                    CreatedCount = totalCreated,
                    UpdatedCount = totalUpdated,
                    M2MCount = relationshipsProcessed > 0 ? relationshipsProcessed : null,
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

                // Preserve all previously accumulated entity errors, plus add this exception error
                var exceptionError = new MigrationError
                {
                    Phase = MigrationPhase.Importing,
                    Message = safeMessage
                };

                // Stream exception error via callback
                options?.ErrorCallback?.Invoke(exceptionError);

                return new ImportResult
                {
                    Success = false,
                    TiersProcessed = plan.TierCount,
                    RecordsImported = totalImported,
                    Duration = stopwatch.Elapsed,
                    EntityResults = entityResults.ToArray(),
                    Errors = errors.Append(exceptionError).ToArray(),
                    PoolStatistics = _connectionPool.Statistics,
                    Warnings = warnings.GetWarnings()
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
            IReadOnlyDictionary<string, FieldValidity> fieldMetadata,
            IdMappingCollection idMappings,
            ImportOptions options,
            IProgressReporter? progress,
            IWarningCollector warnings,
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
                // Track if we fell back to individual operations (for warning emission)
                var wasKnownBulkNotSupported = _prober.IsKnownBulkNotSupported(entityName);

                var operationType = options.Mode switch
                {
                    ImportMode.Create => BulkOperationType.Create,
                    ImportMode.Update => BulkOperationType.Update,
                    _ => BulkOperationType.Upsert
                };

                bulkResult = await _prober.ExecuteWithProbeAsync(
                    entityName,
                    preparedRecords,
                    operationType,
                    bulkOptions,
                    async (_, recs) => await ExecuteIndividualOperationsAsync(entityName, recs.ToList(), options, cancellationToken).ConfigureAwait(false),
                    progressAdapter,
                    cancellationToken).ConfigureAwait(false);

                // Emit warning if bulk fallback occurred during this call (not previously known)
                if (!wasKnownBulkNotSupported && _prober.IsKnownBulkNotSupported(entityName))
                {
                    warnings.AddWarning(new ImportWarning
                    {
                        Code = ImportWarningCodes.BulkNotSupported,
                        Entity = entityName,
                        Message = "Entity does not support bulk operations, fell back to individual operations",
                        Impact = $"Reduced throughput for {preparedRecords.Count} records"
                    });

                    _logger?.LogInformation("Successfully imported {SuccessCount}/{TotalCount} {Entity} records via individual operations",
                        bulkResult.SuccessCount, preparedRecords.Count, entityName);
                }
            }
            else
            {
                // UseBulkApis is false - use individual operations directly
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
                RecordId = e.RecordId,
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
                            var createRequest = new CreateRequest { Target = record };
                            createRequest.ApplyBypassOptions(options);
                            var createResponse = (CreateResponse)await client.ExecuteAsync(createRequest).ConfigureAwait(false);
                            newId = createResponse.id;
                            break;
                        case ImportMode.Update:
                            var updateRequest = new UpdateRequest { Target = record };
                            updateRequest.ApplyBypassOptions(options);
                            await client.ExecuteAsync(updateRequest).ConfigureAwait(false);
                            newId = record.Id;
                            break;
                        default:
                            var upsertRequest = new UpsertRequest { Target = record };
                            upsertRequest.ApplyBypassOptions(options);
                            var upsertResponse = (UpsertResponse)await client.ExecuteAsync(upsertRequest).ConfigureAwait(false);
                            newId = upsertResponse.Target?.Id ?? record.Id;
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
                        Message = ConnectionStringRedactor.RedactExceptionMessage(ex.Message)
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
            IReadOnlyDictionary<string, FieldValidity> fieldMetadata,
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
                if (!_schemaValidator.ShouldIncludeField(attr.Key, options.Mode, fieldMetadata, out _))
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

            // Force team.isdefault to false to prevent conflicts with existing default teams
            // This matches CMT behavior - default teams should not be imported as defaults
            if (record.LogicalName.Equals("team", StringComparison.OrdinalIgnoreCase) &&
                prepared.Contains("isdefault"))
            {
                prepared["isdefault"] = false;
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

                // No explicit mapping found - check for current user fallback
                if (options.UserMappings.UseCurrentUserAsDefault && options.CurrentUserId.HasValue)
                {
                    _logger?.LogDebug("User {UserId} not found in mappings, using current user fallback", er.Id);
                    return new EntityReference("systemuser", options.CurrentUserId.Value);
                }

                // User mapping exists but no mapping found and no fallback available
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
    }
}
