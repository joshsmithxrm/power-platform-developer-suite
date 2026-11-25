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
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Security;
using PPDS.Migration.Analysis;
using PPDS.Migration.Constants;
using PPDS.Migration.DependencyInjection;
using PPDS.Migration.Formats;
using PPDS.Migration.Import.Handlers;
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
        private readonly IReadOnlyList<IRecordFilter> _recordFilters;
        private readonly IReadOnlyList<IRecordTransformer> _recordTransformers;
        private readonly IReadOnlyList<IStateTransitionHandler> _stateTransitionHandlers;
        private readonly IReadOnlyList<IPostImportHandler> _postImportHandlers;
        private readonly StateTransitionProcessor? _stateTransitionProcessor;
        private readonly FileColumnProcessor? _fileColumnProcessor;
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
            _recordFilters = Array.Empty<IRecordFilter>();
            _recordTransformers = Array.Empty<IRecordTransformer>();
            _stateTransitionHandlers = Array.Empty<IStateTransitionHandler>();
            _postImportHandlers = Array.Empty<IPostImportHandler>();
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
            IEnumerable<IRecordFilter>? recordFilters = null,
            IEnumerable<IRecordTransformer>? recordTransformers = null,
            IEnumerable<IStateTransitionHandler>? stateTransitionHandlers = null,
            IEnumerable<IPostImportHandler>? postImportHandlers = null,
            StateTransitionProcessor? stateTransitionProcessor = null,
            FileColumnProcessor? fileColumnProcessor = null,
            IOptions<MigrationOptions>? migrationOptions = null,
            IPluginStepManager? pluginStepManager = null,
            ILogger<TieredImporter>? logger = null)
            : this(connectionPool, bulkExecutor, dataReader, graphBuilder, planBuilder,
                   schemaValidator, deferredFieldProcessor, relationshipProcessor, prober)
        {
            _recordFilters = recordFilters?.ToList() ?? (IReadOnlyList<IRecordFilter>)Array.Empty<IRecordFilter>();
            _recordTransformers = recordTransformers?.ToList() ?? (IReadOnlyList<IRecordTransformer>)Array.Empty<IRecordTransformer>();
            _stateTransitionHandlers = stateTransitionHandlers?.ToList() ?? (IReadOnlyList<IStateTransitionHandler>)Array.Empty<IStateTransitionHandler>();
            _postImportHandlers = postImportHandlers?.ToList() ?? (IReadOnlyList<IPostImportHandler>)Array.Empty<IPostImportHandler>();
            _stateTransitionProcessor = stateTransitionProcessor;
            _fileColumnProcessor = fileColumnProcessor;
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
            progress ??= IProgressReporter.Silent;
            EnsureModeIsImplemented(options ?? _defaultOptions);

            progress.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = "Reading data archive..."
            });

            var data = await _dataReader.ReadAsync(dataPath, progress, cancellationToken).ConfigureAwait(false);

            progress.Report(new ProgressEventArgs
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

            progress ??= IProgressReporter.Silent;
            options ??= _defaultOptions;
            EnsureModeIsImplemented(options);

            if (options.ImpersonateOwners && options.UserMappings == null)
            {
                throw new InvalidOperationException(
                    "ImpersonateOwners requires UserMappings to be configured. " +
                    "Provide a user mapping file with source-to-target user ID mappings.");
            }

            var stopwatch = Stopwatch.StartNew();
            var idMappings = new IdMappingCollection();
            var entityResults = new ConcurrentBag<EntityImportResult>();
            var errors = new ConcurrentBag<MigrationError>();
            var warnings = new WarningCollector();
            var totalImported = 0;

            _logger?.LogInformation("Starting tiered import: {Tiers} tiers, {Records} records",
                plan.TierCount, data.TotalRecordCount);

            // Load and validate schema
            var targetFieldMetadata = await ValidateSchemaAndHandleMissingColumnsAsync(
                data, options, progress, warnings, cancellationToken).ConfigureAwait(false);

            // Create shared import context for phase processors
            var context = new ImportContext(data, plan, options, idMappings, targetFieldMetadata, progress)
            {
                OutputManager = options.OutputManager,
                Warnings = warnings
            };

            // Select the plug-in steps of tables with disableplugins=true. Read-only: the tables are
            // resolved in the TARGET by logical name, and an unresolved table refuses the import
            // here, before any step is disabled or any record written (#1921).
            var pluginStepsToDisable = await FindPluginStepsToDisableAsync(
                data, options, cancellationToken).ConfigureAwait(false);

            // Every step that may have been disabled, added before its update is sent, so the
            // re-enable below restores it whether the import ends, fails or is cancelled, also
            // when that happens while the steps are still being disabled.
            var disabledPluginSteps = new List<Guid>();

            // Track whether we've re-enabled plugins so the finally-block fallback
            // doesn't double-run after the inline call on the happy/failure paths.
            var pluginsReenabled = false;

            try
            {
                await DisablePluginsForImportAsync(
                    pluginStepsToDisable, disabledPluginSteps, progress, cancellationToken).ConfigureAwait(false);

                // Phase 1: Process tiers
                var tierResult = await ProcessTiersAsync(
                    context, entityResults, errors, warnings, cancellationToken).ConfigureAwait(false);
                totalImported = tierResult.TotalImported;
                var phase1Duration = stopwatch.Elapsed;

                // Phase 2: Process deferred fields
                context.OutputManager?.LogProgress("Starting deferred fields phase");
                var deferredResult = await _deferredFieldProcessor.ProcessAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                var phase2Duration = deferredResult.Duration;

                // Phase 2.5: Upload file column data (before state transitions —
                // records must still be mutable; closed/inactive records reject file uploads)
                PhaseResult fileColumnResult;
                if (_fileColumnProcessor != null && data.FileData.Count > 0)
                {
                    context.OutputManager?.LogProgress("Starting file column upload phase");
                    fileColumnResult = await _fileColumnProcessor.ProcessAsync(context, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    fileColumnResult = PhaseResult.Skipped();
                }

                // Phase 3: Process state transitions
                PhaseResult stateTransitionResult;
                if (_stateTransitionProcessor != null)
                {
                    context.OutputManager?.LogProgress("Starting state transitions phase");
                    stateTransitionResult = await _stateTransitionProcessor.ProcessAsync(context, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    stateTransitionResult = PhaseResult.Skipped();
                }

                // Run post-import handlers after state transitions
                foreach (var handler in _postImportHandlers)
                {
                    foreach (var entitySchema in data.Schema.Entities)
                    {
                        if (handler.CanHandle(entitySchema.LogicalName))
                        {
                            try
                            {
                                await handler.ExecuteAsync(entitySchema.LogicalName, context, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger?.LogWarning(ex,
                                    "Post-import handler {Handler} failed for entity {Entity}",
                                    handler.GetType().Name, entitySchema.LogicalName);
                            }
                        }
                    }
                }

                // Phase 4: Process M2M relationships
                context.OutputManager?.LogProgress("Starting M2M relationships phase");
                var relationshipResult = await _relationshipProcessor.ProcessAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                var phase4Duration = relationshipResult.Duration;

                stopwatch.Stop();

                _logger?.LogInformation("Import complete: {Records} imported, {Deferred} deferred, {Transitions} transitions, {M2M} relationships, {Files} files in {Duration}",
                    totalImported, deferredResult.SuccessCount, stateTransitionResult.SuccessCount, relationshipResult.SuccessCount, fileColumnResult.SuccessCount, stopwatch.Elapsed);

                // D5: re-enable plugins before building the result so any re-enable
                // warnings (PluginStepReenableException → per-step warnings) are
                // captured in ImportResult.Warnings.
                await EnablePluginsAfterImportAsync(disabledPluginSteps, pluginStepsToDisable, progress, warnings).ConfigureAwait(false);
                pluginsReenabled = true;

                return BuildImportResult(
                    plan, entityResults, errors, warnings,
                    totalImported, data.TotalRecordCount, deferredResult, relationshipResult,
                    stopwatch.Elapsed, phase1Duration, phase2Duration, phase4Duration,
                    options, progress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not ImportRefusedException)
            {
                stopwatch.Stop();
                _logger?.LogError(ex, "Import failed");

                // Same as happy path: re-enable before building the failure result
                // so post-import plugin state is visible in the surfaced warnings.
                await EnablePluginsAfterImportAsync(disabledPluginSteps, pluginStepsToDisable, progress, warnings).ConfigureAwait(false);
                pluginsReenabled = true;

                return BuildFailureResult(
                    plan, entityResults, errors, warnings,
                    totalImported, data.TotalRecordCount, stopwatch.Elapsed, ex, options, progress);
            }
            finally
            {
                // Fallback: only fires if the try/catch above didn't reach the inline
                // re-enable (e.g. OperationCanceledException). Plugins must not stay
                // silently disabled if import is cancelled mid-flight.
                if (!pluginsReenabled)
                {
                    await EnablePluginsAfterImportAsync(disabledPluginSteps, pluginStepsToDisable, progress, warnings).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Validates schema and handles missing columns based on options.
        /// </summary>
        private async Task<FieldMetadataCollection> ValidateSchemaAndHandleMissingColumnsAsync(
            MigrationData data,
            ImportOptions options,
            IProgressReporter progress,
            IWarningCollector warnings,
            CancellationToken cancellationToken)
        {
            var entityNames = data.Schema.Entities.Select(e => e.LogicalName).ToList();
            var targetFieldMetadata = await _schemaValidator.LoadTargetFieldMetadataAsync(
                entityNames, progress, cancellationToken).ConfigureAwait(false);

            var mismatchResult = _schemaValidator.DetectMissingColumns(data, targetFieldMetadata);
            if (!mismatchResult.HasMissingColumns)
            {
                return targetFieldMetadata;
            }

            if (!options.SkipMissingColumns)
            {
                _logger?.LogError("Schema mismatch detected: {Count} columns missing in target",
                    mismatchResult.TotalMissingCount);

                progress.Report(new ProgressEventArgs
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

            progress.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Warning: Skipping {mismatchResult.TotalMissingCount} column(s) not found in target"
            });

            return targetFieldMetadata;
        }

        /// <summary>
        /// Refuses an import mode the importer does not implement for a whole import.
        /// </summary>
        /// <remarks>
        /// <see cref="ImportMode.Skip"/> means "do not import this table" and is set per table in the
        /// schema (<c>importMode="skip"</c>). As the mode of the whole import it used to fall through to
        /// Upsert and overwrite every existing record (#1914); it is refused before anything is read.
        /// </remarks>
        private static void EnsureModeIsImplemented(ImportOptions options)
        {
            if (options.Mode is ImportMode.Create or ImportMode.Update or ImportMode.Upsert)
            {
                return;
            }

            if (options.Mode == ImportMode.Skip)
            {
                throw new ImportRefusedException(
                    ImportRefusalReasons.SkipIsPerTable,
                    "Import mode Skip applies to one table, set with importMode=\"skip\" on that table in the schema. " +
                    "As the mode for the whole import it would import nothing. Nothing was read or written. " +
                    "Choose Create, Update or Upsert, and mark the tables to leave out with importMode=\"skip\".");
            }

            throw new ImportRefusedException(
                ImportRefusalReasons.SkipIsPerTable,
                "The import mode is not one of Create, Update or Upsert. Nothing was read or written.");
        }

        /// <summary>
        /// Finds the active plug-in steps of the tables marked with disableplugins=true, resolved in
        /// the target environment by logical name. Reads only.
        /// </summary>
        private async Task<IReadOnlyList<PluginStepInfo>> FindPluginStepsToDisableAsync(
            MigrationData data,
            ImportOptions options,
            CancellationToken cancellationToken)
        {
            if (!options.RespectDisablePluginsSetting || _pluginStepManager == null)
            {
                return Array.Empty<PluginStepInfo>();
            }

            // By logical name, never by the schema's object type code: that code is the SOURCE
            // environment's, and custom tables have different codes in each environment (#1921).
            var tables = data.Schema.Entities
                .Where(e => e.DisablePlugins && e.ImportMode != ImportMode.Skip)
                .Select(e => e.LogicalName)
                .ToList();

            if (tables.Count == 0)
            {
                return Array.Empty<PluginStepInfo>();
            }

            return await _pluginStepManager.GetActivePluginStepsAsync(tables, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Disables the given plug-in steps, recording each one in <paramref name="disabledPluginSteps"/>.
        /// </summary>
        private async Task DisablePluginsForImportAsync(
            IReadOnlyList<PluginStepInfo> steps,
            List<Guid> disabledPluginSteps,
            IProgressReporter progress,
            CancellationToken cancellationToken)
        {
            if (steps.Count == 0 || _pluginStepManager == null)
            {
                return;
            }

            progress.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Disabling {steps.Count} plugin step(s)..."
            });

            await _pluginStepManager.DisablePluginStepsAsync(
                steps.Select(st => st.Id), disabledPluginSteps, cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation("Disabled {Count} plugin steps", disabledPluginSteps.Count);
        }

        /// <summary>
        /// Re-enables plugins that were disabled during import.
        /// </summary>
        private async Task EnablePluginsAfterImportAsync(
            IReadOnlyList<Guid> disabledPluginSteps,
            IReadOnlyList<PluginStepInfo> stepInfo,
            IProgressReporter progress,
            IWarningCollector warnings)
        {
            if (disabledPluginSteps.Count == 0 || _pluginStepManager == null)
            {
                return;
            }

            progress.Report(new ProgressEventArgs
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
            catch (PluginStepReenableException ex)
            {
                // D5: per-step failures are surfaced explicitly so callers can detect
                // that some plugin steps remain disabled post-import.
                _logger?.LogError(ex,
                    "Failed to re-enable {FailedCount}/{TotalCount} plugin steps — they remain disabled",
                    ex.Failures.Count, disabledPluginSteps.Count);

                foreach (var (stepId, error) in ex.Failures)
                {
                    var stepName = DescribeStep(stepId, stepInfo);
                    warnings.AddWarning(new ImportWarning
                    {
                        Code = ImportWarningCodes.PluginReenableFailed,
                        Message = $"Plugin step {stepName} could not be re-enabled: {error.Message}",
                        Impact = "This plugin step remains disabled."
                    });

                    // Also through the progress reporter: on a cancelled import no result is
                    // returned, so this is the only place the user sees which step to fix.
                    progress.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Complete,
                        Message = $"Warning: plugin step {stepName} could not be re-enabled and remains disabled. Re-enable it manually."
                    });
                }

                // Also emit a roll-up warning for dashboards/aggregators.
                warnings.AddWarning(new ImportWarning
                {
                    Code = ImportWarningCodes.PluginReenableFailed,
                    Message = $"{ex.Failures.Count} of {disabledPluginSteps.Count} plugin step(s) failed to re-enable after import.",
                    Impact = $"{ex.Failures.Count} plugin step(s) remain disabled and must be re-enabled manually."
                });
            }
            catch (Exception ex)
            {
                // Bulk/pool/token failure — no per-step detail available. Surface everything.
                _logger?.LogError(ex,
                    "Failed to re-enable plugin steps — {Count} step(s) may remain disabled",
                    disabledPluginSteps.Count);
                var names = string.Join(", ", disabledPluginSteps.Select(id => DescribeStep(id, stepInfo)));
                warnings.AddWarning(new ImportWarning
                {
                    Code = ImportWarningCodes.PluginReenableFailed,
                    Message = $"Failed to re-enable {disabledPluginSteps.Count} plugin steps ({names}): {ex.Message}",
                    Impact = $"{disabledPluginSteps.Count} plugin step(s) may remain disabled and must be re-enabled manually."
                });
                progress.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Complete,
                    Message = $"Warning: these plugin steps may remain disabled; re-enable them manually: {names}"
                });
            }
        }

        private static string DescribeStep(Guid stepId, IReadOnlyList<PluginStepInfo> stepInfo)
        {
            var name = stepInfo.FirstOrDefault(st => st.Id == stepId)?.Name;
            return string.IsNullOrEmpty(name) ? stepId.ToString() : $"'{name}' ({stepId})";
        }

        /// <summary>
        /// Processes all tiers sequentially, importing entities within each tier in parallel.
        /// </summary>
        private async Task<TierProcessingResult> ProcessTiersAsync(
            ImportContext context,
            ConcurrentBag<EntityImportResult> entityResults,
            ConcurrentBag<MigrationError> errors,
            IWarningCollector warnings,
            CancellationToken cancellationToken)
        {
            var totalEntities = context.Data.EntityData.Count;
            var overallTotal = context.Data.TotalRecordCount;
            long overallProcessed = 0;
            var totalImported = 0;

            foreach (var tier in context.Plan.Tiers)
            {
                var tierStopwatch = Stopwatch.StartNew();
                var tierRecordsSuccess = 0L;
                var tierRecordsFailed = 0L;

                context.OutputManager?.LogTierStart(tier.TierNumber, tier.Entities.ToArray());

                context.Progress.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Importing,
                    TierNumber = tier.TierNumber,
                    TotalTiers = context.Plan.TierCount,
                    TotalEntities = totalEntities,
                    OverallProcessed = Interlocked.Read(ref overallProcessed),
                    OverallTotal = overallTotal,
                    Message = $"Processing tier {tier.TierNumber}: {string.Join(", ", tier.Entities)}"
                });

                await Parallel.ForEachAsync(
                    tier.Entities,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = context.Options.MaxParallelEntities,
                        CancellationToken = cancellationToken
                    },
                    async (entityName, ct) =>
                    {
                        if (!context.Data.EntityData.TryGetValue(entityName, out var records) || records.Count == 0)
                        {
                            return;
                        }

                        // Check per-entity import mode override (AC-37, AC-38)
                        var entitySchema = context.Data.Schema.Entities
                            .FirstOrDefault(e => e.LogicalName.Equals(entityName, StringComparison.OrdinalIgnoreCase));
                        if (entitySchema?.ImportMode == ImportMode.Skip)
                        {
                            _logger?.LogInformation("Skipping entity {Entity} (ImportMode=Skip)", entityName);
                            entityResults.Add(new EntityImportResult
                            {
                                EntityLogicalName = entityName,
                                TierNumber = tier.TierNumber,
                                RecordCount = records.Count,
                                SkippedCount = records.Count,
                                CreatedCount = 0,
                                UpdatedCount = 0,
                                Success = true
                            });
                            Interlocked.Add(ref overallProcessed, records.Count);
                            return;
                        }

                        // Determine effective import mode for this entity
                        var effectiveMode = entitySchema?.ImportMode ?? context.Options.Mode;

                        context.Plan.DeferredFields.TryGetValue(entityName, out var deferredFields);
                        var entityFieldMetadata = context.TargetFieldMetadata.GetFieldsForEntity(entityName);

                        var result = await ImportEntityAsync(
                            entityName,
                            records,
                            tier.TierNumber,
                            deferredFields,
                            entityFieldMetadata,
                            context,
                            effectiveMode,
                            warnings,
                            ct).ConfigureAwait(false);

                        entityResults.Add(result);
                        Interlocked.Add(ref totalImported, result.SuccessCount);

                        Interlocked.Add(ref tierRecordsSuccess, result.SuccessCount);
                        Interlocked.Add(ref tierRecordsFailed, result.FailureCount);

                        Interlocked.Add(ref overallProcessed, result.RecordCount);

                        if (result.Duration.TotalSeconds > 0)
                        {
                            var rps = result.SuccessCount / result.Duration.TotalSeconds;
                            context.OutputManager?.LogEntityProgress(entityName, result.SuccessCount, records.Count, rps);
                        }

                        if (result.FailureCount > 0)
                        {
                            var errorSummary = result.Errors.FirstOrDefault()?.Message ?? "Multiple errors";
                            context.OutputManager?.LogEntityError(entityName, result.FailureCount, errorSummary);
                        }

                        foreach (var error in result.Errors)
                        {
                            errors.Add(error);
                            context.Options.ErrorCallback?.Invoke(error);
                        }
                    }).ConfigureAwait(false);

                tierStopwatch.Stop();
                var tierDuration = tierStopwatch.Elapsed;
                var tierRps = tierDuration.TotalSeconds > 0
                    ? tierRecordsSuccess / tierDuration.TotalSeconds
                    : 0;

                var tierSummaryMessage = tierRecordsFailed > 0
                    ? $"Tier {tier.TierNumber} completed: {tier.Entities.Count} entities, {tierRecordsSuccess:N0} records ({tierRecordsFailed:N0} failed) in {tierDuration:mm\\:ss} @ {tierRps:F0} rec/s"
                    : $"Tier {tier.TierNumber} completed: {tier.Entities.Count} entities, {tierRecordsSuccess:N0} records in {tierDuration:mm\\:ss} @ {tierRps:F0} rec/s";

                context.Progress.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Importing,
                    TierNumber = tier.TierNumber,
                    TotalTiers = context.Plan.TierCount,
                    Message = tierSummaryMessage
                });

                context.OutputManager?.LogProgress(tierSummaryMessage);

                _logger?.LogInformation("Tier {Tier} complete: {Entities} entities, {Records} records in {Duration}",
                    tier.TierNumber, tier.Entities.Count, tierRecordsSuccess, tierDuration);
            }

            return new TierProcessingResult { TotalImported = totalImported };
        }

        /// <summary>
        /// Builds the successful import result.
        /// </summary>
        private ImportResult BuildImportResult(
            ExecutionPlan plan,
            ConcurrentBag<EntityImportResult> entityResults,
            ConcurrentBag<MigrationError> errors,
            IWarningCollector warnings,
            int totalImported,
            int sourceRecordCount,
            PhaseResult deferredResult,
            PhaseResult relationshipResult,
            TimeSpan duration,
            TimeSpan phase1Duration,
            TimeSpan phase2Duration,
            TimeSpan phase3Duration,
            ImportOptions options,
            IProgressReporter progress)
        {
            var result = new ImportResult
            {
                Success = errors.IsEmpty,
                TiersProcessed = plan.TierCount,
                SourceRecordCount = sourceRecordCount,
                RecordsImported = totalImported,
                RecordsUpdated = deferredResult.SuccessCount,
                RelationshipsProcessed = relationshipResult.SuccessCount,
                RelationshipsFailed = relationshipResult.FailureCount,
                Duration = duration,
                Phase1Duration = phase1Duration,
                Phase2Duration = phase2Duration,
                Phase3Duration = phase3Duration,
                EntityResults = entityResults.ToArray(),
                Errors = errors.ToArray(),
                PoolStatistics = _connectionPool.Statistics,
                Warnings = warnings.GetWarnings()
            };

            // Aggregate entity result stats in a single pass for efficiency
            var recordFailureCount = 0;
            var createdAgg = 0;
            var updatedAgg = 0;
            var hasCreated = false;
            var hasUpdated = false;

            foreach (var r in entityResults)
            {
                recordFailureCount += r.FailureCount;
                if (r.CreatedCount.HasValue)
                {
                    hasCreated = true;
                    createdAgg += r.CreatedCount.Value;
                }
                if (r.UpdatedCount.HasValue)
                {
                    hasUpdated = true;
                    updatedAgg += r.UpdatedCount.Value;
                }
            }

            // A total is known only when every table that wrote records reported it.
            var totalCreated = hasCreated && entityResults.All(r => r.CreatedCount.HasValue) ? (int?)createdAgg : null;
            var totalUpdated = hasUpdated && entityResults.All(r => r.UpdatedCount.HasValue) ? (int?)updatedAgg : null;
            var totalFailureCount = recordFailureCount + relationshipResult.FailureCount;
            var totalSkipped = entityResults.Sum(r => r.SkippedCount);

            result.CreatedCount = totalCreated;
            result.UpdatedCount = totalUpdated;
            result.SkippedCount = totalSkipped;
            result.FailureCount = recordFailureCount;

            progress.Complete(new MigrationResult
            {
                Success = result.Success,
                SourceRecordCount = sourceRecordCount,
                RecordsProcessed = result.RecordsImported + result.RecordsUpdated + totalFailureCount + relationshipResult.SuccessCount,
                SuccessCount = result.RecordsImported + result.RecordsUpdated + relationshipResult.SuccessCount,
                FailureCount = totalFailureCount,
                CreatedCount = totalCreated,
                UpdatedCount = totalUpdated,
                SkippedCount = totalSkipped,
                M2MCount = relationshipResult.SuccessCount > 0 ? relationshipResult.SuccessCount : null,
                RelationshipsFailed = relationshipResult.FailureCount > 0 ? relationshipResult.FailureCount : null,
                PoolStatistics = _connectionPool.Statistics,
                Duration = result.Duration,
                Errors = errors.ToArray()
            });

            return result;
        }

        /// <summary>
        /// Builds the failure import result when an exception occurs.
        /// </summary>
        private ImportResult BuildFailureResult(
            ExecutionPlan plan,
            ConcurrentBag<EntityImportResult> entityResults,
            ConcurrentBag<MigrationError> errors,
            IWarningCollector warnings,
            int totalImported,
            int sourceRecordCount,
            TimeSpan duration,
            Exception ex,
            ImportOptions options,
            IProgressReporter progress)
        {
            var safeMessage = ConnectionStringRedactor.RedactExceptionMessage(ex.Message);
            progress.Error(ex, "Import failed");

            var exceptionError = new MigrationError
            {
                Phase = MigrationPhase.Importing,
                Message = safeMessage
            };

            options.ErrorCallback?.Invoke(exceptionError);

            return new ImportResult
            {
                Success = false,
                TiersProcessed = plan.TierCount,
                SourceRecordCount = sourceRecordCount,
                RecordsImported = totalImported,
                SkippedCount = entityResults.Sum(r => r.SkippedCount),
                FailureCount = entityResults.Sum(r => r.FailureCount),
                Duration = duration,
                EntityResults = entityResults.ToArray(),
                Errors = errors.Append(exceptionError).ToArray(),
                PoolStatistics = _connectionPool.Statistics,
                Warnings = warnings.GetWarnings()
            };
        }

        /// <summary>
        /// Result of processing all tiers.
        /// </summary>
        private readonly struct TierProcessingResult
        {
            public int TotalImported { get; init; }
        }

        private async Task<EntityImportResult> ImportEntityAsync(
            string entityName,
            IReadOnlyList<Entity> records,
            int tierNumber,
            IReadOnlyList<string>? deferredFields,
            IReadOnlyDictionary<string, FieldValidity> fieldMetadata,
            ImportContext context,
            ImportMode effectiveMode,
            IWarningCollector warnings,
            CancellationToken cancellationToken)
        {
            var entityStopwatch = Stopwatch.StartNew();
            var deferredSet = deferredFields != null
                ? new HashSet<string>(deferredFields, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var options = context.Options;
            var idMappings = context.IdMappings;
            var progress = context.Progress;

            _logger?.LogDebug("Importing {Count} records for {Entity}", records.Count, entityName);

            // Compute elapsed time for date shifting
            var elapsed = context.Data.ExportedAt != default
                ? DateTime.UtcNow - context.Data.ExportedAt
                : TimeSpan.Zero;

            // Look up field schemas for date mode
            var entitySchema = context.Data.Schema.Entities
                .FirstOrDefault(e => e.LogicalName.Equals(entityName, StringComparison.OrdinalIgnoreCase));
            var dateFieldModes = new Dictionary<string, DateMode>(StringComparer.OrdinalIgnoreCase);
            if (entitySchema != null)
            {
                foreach (var field in entitySchema.Fields)
                {
                    if (field.DateMode != DateMode.Absolute)
                    {
                        dateFieldModes[field.LogicalName] = field.DateMode;
                    }
                }
            }

            // Prepare records: apply handlers, remap lookups, null deferred fields, filter
            var preparedRecords = new List<Entity>();
            // State transitions are held here and handed to the state phase only for records
            // proven written below: a record that failed (in Create mode, one that already exists)
            // or that a filter left out must not have its state changed (#1914 review MF1, MF2).
            var pendingTransitions = new List<(Guid RecordId, StateTransitionData Data)>();
            List<Guid?>? ownerMappingList = options.ImpersonateOwners ? new List<Guid?>() : null;
            foreach (var record in records)
            {
                // Step 1: Apply record filters
                var shouldSkip = false;
                foreach (var filter in _recordFilters)
                {
                    if (filter.CanHandle(entityName) && filter.ShouldSkip(record, context))
                    {
                        shouldSkip = true;
                        break;
                    }
                }
                if (shouldSkip) continue;

                // Step 2: Collect state transitions (before stripping statecode/statuscode)
                // Use first matching handler only — each entity has at most one handler by design.
                var stateHandler = _stateTransitionHandlers.FirstOrDefault(h => h.CanHandle(entityName));
                if (stateHandler != null)
                {
                    var transition = stateHandler.GetTransition(record, context);
                    if (transition != null)
                    {
                        pendingTransitions.Add((record.Id, transition));
                    }
                }

                // Step 2b: Generic state transition collection for entities without handlers
                if (stateHandler == null)
                {
                    var sc = record.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0;
                    var stc = record.GetAttributeValue<OptionSetValue>("statuscode")?.Value ?? -1;
                    if (sc != 0)
                    {
                        pendingTransitions.Add((record.Id, new StateTransitionData
                        {
                            EntityName = entityName,
                            RecordId = record.Id,
                            StateCode = sc,
                            StatusCode = stc
                        }));
                    }
                }

                // Step 3: Strip statecode and statuscode from ALL records (AC-05)
                record.Attributes.Remove("statecode");
                record.Attributes.Remove("statuscode");

                // Step 4: Apply record transformers
                var transformed = record;
                foreach (var transformer in _recordTransformers)
                {
                    if (transformer.CanHandle(entityName))
                    {
                        transformed = transformer.Transform(transformed, context);
                    }
                }

                // Step 5: Apply date shifting for non-absolute DateMode fields
                if (elapsed != TimeSpan.Zero && dateFieldModes.Count > 0)
                {
                    foreach (var (fieldName, mode) in dateFieldModes)
                    {
                        if (transformed.Contains(fieldName) && transformed[fieldName] is DateTime dtValue)
                        {
                            var shifted = DateShifter.Shift(dtValue, mode, elapsed);
                            if (shifted.HasValue)
                            {
                                transformed[fieldName] = shifted.Value;
                            }
                        }
                    }
                }

                // Extract owner mapping BEFORE PrepareRecordForImport may strip ownerid
                // Built alongside preparedRecords to keep indices aligned after filtering
                if (ownerMappingList != null)
                {
                    Guid? mappedOwner = null;
                    if (record.Contains("ownerid") && record["ownerid"] is EntityReference ownerRef)
                    {
                        if (options.UserMappings!.TryGetMappedUserId(ownerRef.Id, out var mappedId))
                        {
                            mappedOwner = mappedId;
                        }
                    }
                    ownerMappingList.Add(mappedOwner);
                }

                var prepared = PrepareRecordForImport(transformed, deferredSet, fieldMetadata, idMappings, options, effectiveMode);
                preparedRecords.Add(prepared);
            }

            // Convert to array for ExecuteWithOwnerGroupingAsync
            var ownerMapping = ownerMappingList?.ToArray();

            // Create progress adapter that bridges BulkOperationExecutor progress to IProgressReporter
            var progressAdapter = ProgressAdapterFactory.Create(progress, snapshot => new ProgressEventArgs
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

                var operationType = ToBulkOperationType(effectiveMode);

                if (options.ImpersonateOwners && ownerMapping != null)
                {
                    // Group records by mapped owner for impersonation
                    bulkResult = await ExecuteWithOwnerGroupingAsync(
                        entityName, preparedRecords, ownerMapping, operationType, bulkOptions,
                        effectiveMode, options, progressAdapter, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    bulkResult = await _prober.ExecuteWithProbeAsync(
                        entityName,
                        preparedRecords,
                        operationType,
                        bulkOptions,
                        null,
                        async (_, recs) => await ExecuteIndividualOperationsAsync(entityName, recs.ToList(), effectiveMode, options, null, cancellationToken).ConfigureAwait(false),
                        progressAdapter,
                        cancellationToken).ConfigureAwait(false);
                }

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
                if (options.ImpersonateOwners && ownerMapping != null)
                {
                    bulkResult = await ExecuteWithOwnerGroupingAsync(
                        entityName, preparedRecords, ownerMapping, ToBulkOperationType(effectiveMode), bulkOptions,
                        effectiveMode, options, null, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // UseBulkApis is false - use individual operations directly
                    bulkResult = await ExecuteIndividualOperationsAsync(entityName, preparedRecords, effectiveMode, options, null, cancellationToken).ConfigureAwait(false);
                }
            }

            // Map only the records PROVEN written. The later phases (deferred fields, state
            // transitions, many-to-many, file columns) follow these mappings, so a record that
            // failed, or that a filter left out, gets nothing more (#1914 review MF1, MF2). IDs are
            // preserved from Entity.Id, so a written record maps to itself.
            var written = ProvenWrittenRecords(preparedRecords, bulkResult, out var notProvenReason);
            var laterPhasesSkipped = written == null;
            if (written != null)
            {
                foreach (var id in written)
                {
                    idMappings.AddMapping(entityName, id, id);
                }

                foreach (var (recordId, transition) in pendingTransitions)
                {
                    if (written.Contains(recordId))
                    {
                        context.StateTransitions.Add(entityName, recordId, transition);
                    }
                }
            }
            else
            {
                _logger?.LogWarning("Not running the later phases for {Entity}: {Reason}", entityName, notProvenReason);
                warnings.AddWarning(new ImportWarning
                {
                    Code = ImportWarningCodes.LaterPhasesSkipped,
                    Entity = entityName,
                    Message = $"The result of writing {entityName} does not say which records were written ({notProvenReason}), " +
                              "so its deferred lookups, state changes, many-to-many associations and file data were not sent.",
                    Impact = $"{preparedRecords.Count} {entityName} record(s) may be missing lookups, state, associations or files."
                });
            }

            // Convert BulkOperationErrors to MigrationErrors
            var allErrors = bulkResult.Errors.Select(e => new MigrationError
            {
                Phase = MigrationPhase.Importing,
                EntityLogicalName = entityName,
                RecordIndex = e.Index,
                RecordId = e.RecordId,
                ErrorCode = e.ErrorCode,
                Message = e.Message,
                Diagnostics = e.Diagnostics
            }).ToList();

            entityStopwatch.Stop();

            // Created and updated follow from the mode for Create and Update; for Upsert they come
            // from the responses, and stay unknown (null) when the responses did not say.
            var (createdCount, updatedCount) = effectiveMode switch
            {
                ImportMode.Create => ((int?)bulkResult.SuccessCount, (int?)0),
                ImportMode.Update => (0, bulkResult.SuccessCount),
                _ => (bulkResult.CreatedCount, bulkResult.UpdatedCount)
            };

            return new EntityImportResult
            {
                EntityLogicalName = entityName,
                TierNumber = tierNumber,
                RecordCount = records.Count,
                SuccessCount = bulkResult.SuccessCount,
                FailureCount = bulkResult.FailureCount,
                SkippedCount = records.Count - preparedRecords.Count,
                CreatedCount = createdCount,
                UpdatedCount = updatedCount,
                LaterPhasesSkipped = laterPhasesSkipped,
                Duration = entityStopwatch.Elapsed,
                Success = bulkResult.FailureCount == 0 && !laterPhasesSkipped,
                Errors = allErrors
            };
        }

        private async Task<BulkOperationResult> ExecuteIndividualOperationsAsync(
            string entityName,
            List<Entity> records,
            ImportMode effectiveMode,
            ImportOptions options,
            DataverseClientOptions? clientOptions,
            CancellationToken cancellationToken)
        {
            var createdIds = new List<Guid>();
            var errors = new List<BulkOperationError>();
            var successCount = 0;
            var failureCount = 0;
            var upsertCreated = 0;
            var upsertUpdated = 0;

            await using var client = await _connectionPool.GetClientAsync(clientOptions, cancellationToken: cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                try
                {
                    Guid newId;
                    switch (effectiveMode)
                    {
                        case ImportMode.Create:
                            var createRequest = new CreateRequest { Target = record };
                            createRequest.ApplyBypassOptions(options);
                            var createResponse = (CreateResponse)await client.ExecuteAsync(createRequest, cancellationToken).ConfigureAwait(false);
                            newId = createResponse.id;
                            break;
                        case ImportMode.Update:
                            var updateRequest = new UpdateRequest { Target = record };
                            updateRequest.ApplyBypassOptions(options);
                            await client.ExecuteAsync(updateRequest, cancellationToken).ConfigureAwait(false);
                            newId = record.Id;
                            break;
                        case ImportMode.Upsert:
                            var upsertRequest = new UpsertRequest { Target = record };
                            upsertRequest.ApplyBypassOptions(options);
                            var upsertResponse = (UpsertResponse)await client.ExecuteAsync(upsertRequest, cancellationToken).ConfigureAwait(false);
                            newId = upsertResponse.Target?.Id ?? record.Id;
                            if (upsertResponse.RecordCreated)
                            {
                                upsertCreated++;
                            }
                            else
                            {
                                upsertUpdated++;
                            }
                            break;
                        default:
                            // Skip never reaches a write: it is refused for a whole import and
                            // returns before this for a table (#1914). Never fall through to Upsert.
                            throw new InvalidOperationException($"Import mode {effectiveMode} does not write records.");
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
                Duration = TimeSpan.Zero,
                CreatedCount = effectiveMode == ImportMode.Upsert ? upsertCreated : null,
                UpdatedCount = effectiveMode == ImportMode.Upsert ? upsertUpdated : null
            };
        }

        private async Task<BulkOperationResult> ExecuteWithOwnerGroupingAsync(
            string entityName,
            List<Entity> preparedRecords,
            Guid?[] ownerMapping,
            BulkOperationType operationType,
            BulkOperationOptions bulkOptions,
            ImportMode effectiveMode,
            ImportOptions options,
            IProgress<Dataverse.Progress.ProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            // Group record indices by mapped owner
            // Use Guid.Empty as sentinel for unmapped records (no impersonation)
            var groups = new Dictionary<Guid, List<int>>();
            for (int i = 0; i < preparedRecords.Count; i++)
            {
                var owner = ownerMapping[i] ?? Guid.Empty;
                if (!groups.TryGetValue(owner, out var indices))
                {
                    indices = new List<int>();
                    groups[owner] = indices;
                }
                indices.Add(i);
            }

            var mappedOwnerCount = groups.Count(g => g.Key != Guid.Empty);
            var unmappedCount = groups.TryGetValue(Guid.Empty, out var unmappedGroup) ? unmappedGroup.Count : 0;

            _logger?.LogInformation(
                "Impersonation grouping for {Entity}: {Groups} owner groups ({Owners} distinct owners, {Unmapped} unmapped)",
                entityName, groups.Count,
                mappedOwnerCount,
                unmappedCount);

            // Execute each owner group
            var allErrors = new List<BulkOperationError>();
            var totalSuccess = 0;
            var totalFailure = 0;
            int? totalCreated = null;
            int? totalUpdated = null;

            foreach (var (ownerId, indices) in groups)
            {
                var groupRecords = indices.Select(i => preparedRecords[i]).ToList();
                var clientOptions = ownerId != Guid.Empty
                    ? new DataverseClientOptions { CallerId = ownerId }
                    : null;

                BulkOperationResult groupResult;
                if (options.UseBulkApis)
                {
                    groupResult = await _prober.ExecuteWithProbeAsync(
                        entityName,
                        groupRecords,
                        operationType,
                        bulkOptions,
                        clientOptions,
                        async (_, recs) => await ExecuteIndividualOperationsAsync(
                            entityName, recs.ToList(), effectiveMode, options, clientOptions, cancellationToken).ConfigureAwait(false),
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    groupResult = await ExecuteIndividualOperationsAsync(
                        entityName, groupRecords, effectiveMode, options, clientOptions, cancellationToken).ConfigureAwait(false);
                }

                totalSuccess += groupResult.SuccessCount;
                totalFailure += groupResult.FailureCount;

                // Remap error indices back to original positions
                foreach (var error in groupResult.Errors)
                {
                    allErrors.Add(new BulkOperationError
                    {
                        Index = error.Index < indices.Count ? indices[error.Index] : error.Index,
                        RecordId = error.RecordId,
                        ErrorCode = error.ErrorCode,
                        Message = error.Message,
                        FieldName = error.FieldName,
                        FieldValueDescription = error.FieldValueDescription,
                        Diagnostics = error.Diagnostics
                    });
                }

                if (groupResult.CreatedCount.HasValue)
                {
                    totalCreated = (totalCreated ?? 0) + groupResult.CreatedCount.Value;
                }
                if (groupResult.UpdatedCount.HasValue)
                {
                    totalUpdated = (totalUpdated ?? 0) + groupResult.UpdatedCount.Value;
                }
            }

            return new BulkOperationResult
            {
                SuccessCount = totalSuccess,
                FailureCount = totalFailure,
                Errors = allErrors,
                Duration = TimeSpan.Zero,
                CreatedCount = totalCreated,
                UpdatedCount = totalUpdated
            };
        }

        /// <summary>
        /// The ids of the prepared records that the result proves were written, or null when it
        /// cannot say. A failure counts only by its record id: the bulk executor's error index is
        /// relative to a batch, so it cannot name a record of the table. Every occurrence of a failed
        /// id is treated as not written (a duplicate in the file whose second create failed).
        /// </summary>
        internal static HashSet<Guid>? ProvenWrittenRecords(
            IReadOnlyList<Entity> preparedRecords,
            BulkOperationResult result,
            out string? reason)
        {
            reason = null;
            if (result.SuccessCount + result.FailureCount != preparedRecords.Count)
            {
                reason = $"{result.SuccessCount} succeeded and {result.FailureCount} failed of {preparedRecords.Count} sent";
                return null;
            }

            if (result.Errors.Count != result.FailureCount)
            {
                reason = $"{result.FailureCount} failed but {result.Errors.Count} error(s) were returned";
                return null;
            }

            var sent = new HashSet<Guid>(preparedRecords.Select(r => r.Id));
            var failed = new HashSet<Guid>();
            foreach (var error in result.Errors)
            {
                if (error.RecordId is not { } id || id == Guid.Empty || !sent.Contains(id))
                {
                    reason = "a failure does not name a record that was sent";
                    return null;
                }

                failed.Add(id);
            }

            var written = new HashSet<Guid>();
            foreach (var record in preparedRecords)
            {
                if (record.Id != Guid.Empty && !failed.Contains(record.Id))
                {
                    written.Add(record.Id);
                }
            }

            return written;
        }

        /// <summary>
        /// The bulk operation for a mode that writes records. Skip has none: it is refused for a whole
        /// import and handled before this for a table. Nothing falls through to Upsert (#1914).
        /// </summary>
        internal static BulkOperationType ToBulkOperationType(ImportMode mode) => mode switch
        {
            ImportMode.Create => BulkOperationType.Create,
            ImportMode.Update => BulkOperationType.Update,
            ImportMode.Upsert => BulkOperationType.Upsert,
            _ => throw new InvalidOperationException($"Import mode {mode} does not write records.")
        };

        private Entity PrepareRecordForImport(
            Entity record,
            HashSet<string> deferredFields,
            IReadOnlyDictionary<string, FieldValidity> fieldMetadata,
            IdMappingCollection idMappings,
            ImportOptions options,
            ImportMode effectiveMode)
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
                if (!_schemaValidator.ShouldIncludeField(attr.Key, effectiveMode, fieldMetadata, out _))
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
            if (record.LogicalName.Equals(EntityNames.Team, StringComparison.OrdinalIgnoreCase) &&
                prepared.Contains(AttributeNames.IsDefault))
            {
                prepared[AttributeNames.IsDefault] = false;
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
                    return new EntityReference(EntityNames.SystemUser, options.CurrentUserId.Value);
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
            return entityLogicalName.Equals(EntityNames.SystemUser, StringComparison.OrdinalIgnoreCase) ||
                   entityLogicalName.Equals(EntityNames.Team, StringComparison.OrdinalIgnoreCase);
        }
    }
}
