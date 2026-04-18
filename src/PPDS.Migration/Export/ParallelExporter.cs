using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Security;
using PPDS.Migration.DependencyInjection;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Export
{
    /// <summary>
    /// Parallel exporter for Dataverse data.
    /// </summary>
    public class ParallelExporter : IExporter
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly ICmtSchemaReader _schemaReader;
        private readonly ICmtDataWriter _dataWriter;
        private readonly FileColumnTransferHelper? _fileTransferHelper;
        private readonly ExportOptions _defaultOptions;
        private readonly ILogger<ParallelExporter>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ParallelExporter"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="schemaReader">The schema reader.</param>
        /// <param name="dataWriter">The data writer.</param>
        public ParallelExporter(
            IDataverseConnectionPool connectionPool,
            ICmtSchemaReader schemaReader,
            ICmtDataWriter dataWriter)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _schemaReader = schemaReader ?? throw new ArgumentNullException(nameof(schemaReader));
            _dataWriter = dataWriter ?? throw new ArgumentNullException(nameof(dataWriter));
            _defaultOptions = new ExportOptions();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ParallelExporter"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="schemaReader">The schema reader.</param>
        /// <param name="dataWriter">The data writer.</param>
        /// <param name="fileTransferHelper">Optional file column transfer helper for downloading file data.</param>
        /// <param name="migrationOptions">Migration options from DI.</param>
        /// <param name="logger">The logger.</param>
        public ParallelExporter(
            IDataverseConnectionPool connectionPool,
            ICmtSchemaReader schemaReader,
            ICmtDataWriter dataWriter,
            FileColumnTransferHelper? fileTransferHelper = null,
            IOptions<MigrationOptions>? migrationOptions = null,
            ILogger<ParallelExporter>? logger = null)
            : this(connectionPool, schemaReader, dataWriter)
        {
            _fileTransferHelper = fileTransferHelper;
            _defaultOptions = migrationOptions?.Value.Export ?? new ExportOptions();
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<ExportResult> ExportAsync(
            string schemaPath,
            string outputPath,
            ExportOptions? options = null,
            IProgressReporter? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress ??= IProgressReporter.Silent;

            progress.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = "Parsing schema..."
            });

            var schema = await _schemaReader.ReadAsync(schemaPath, cancellationToken).ConfigureAwait(false);

            return await ExportAsync(schema, outputPath, options, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ExportResult> ExportAsync(
            MigrationSchema schema,
            string outputPath,
            ExportOptions? options = null,
            IProgressReporter? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentNullException(nameof(outputPath));

            progress ??= IProgressReporter.Silent;
            options ??= _defaultOptions;
            var stopwatch = Stopwatch.StartNew();
            var entityResults = new ConcurrentBag<EntityExportResult>();
            var entityData = new ConcurrentDictionary<string, IReadOnlyList<Entity>>(StringComparer.OrdinalIgnoreCase);
            var errors = new ConcurrentBag<MigrationError>();

            _logger?.LogInformation("Starting parallel export of {Count} entities with parallelism {Parallelism}",
                schema.Entities.Count, options.DegreeOfParallelism);

            progress.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Exporting,
                Message = $"Exporting {schema.Entities.Count} entities..."
            });

            var warnings = new WarningCollector();

            try
            {
                // Pre-fetch approximate record counts for partition routing.
                // Entities whose count query fails are surfaced via WarningCollector (F1);
                // they will fall back to sequential export because recordCount=0 drops them below threshold.
                var countFailures = new ConcurrentBag<string>();
                var recordCounts = await GetEntityRecordCountsAsync(
                    schema.Entities, countFailures, cancellationToken).ConfigureAwait(false);

                foreach (var failedEntity in countFailures)
                {
                    warnings.AddWarning(new ImportWarning
                    {
                        Code = ExportWarningCodes.CountFailedSequentialFallback,
                        Entity = failedEntity,
                        Message = $"Record count query failed for entity '{failedEntity}' — falling back to sequential export (partitioning disabled).",
                        Impact = "Reduced export throughput for this entity."
                    });

                    progress.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Exporting,
                        Entity = failedEntity,
                        Message = $"Warning: count query failed for {failedEntity} — exporting sequentially (no partitioning)."
                    });
                }

                // Export all entities in parallel
                await Parallel.ForEachAsync(
                    schema.Entities,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.DegreeOfParallelism,
                        CancellationToken = cancellationToken
                    },
                    async (entitySchema, ct) =>
                    {
                        recordCounts.TryGetValue(entitySchema.LogicalName, out var recordCount);
                        var result = await ExportEntityAsync(entitySchema, options, progress, recordCount, ct).ConfigureAwait(false);
                        entityResults.Add(result);

                        if (result.Success && result.Records != null)
                        {
                            entityData[entitySchema.LogicalName] = result.Records;
                        }
                        else if (!result.Success)
                        {
                            errors.Add(new MigrationError
                            {
                                Phase = MigrationPhase.Exporting,
                                EntityLogicalName = entitySchema.LogicalName,
                                Message = result.ErrorMessage ?? "Unknown error"
                            });
                        }
                    }).ConfigureAwait(false);

                // Export M2M relationships
                progress.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Exporting,
                    Message = "Exporting M2M relationships..."
                });

                var relationshipData = await ExportM2MRelationshipsAsync(
                    schema, entityData, options, progress, errors, cancellationToken).ConfigureAwait(false);

                // Download file column data if opted in
                var fileData = new Dictionary<string, IReadOnlyList<FileColumnData>>(StringComparer.OrdinalIgnoreCase);
                if (options.IncludeFileData && _fileTransferHelper != null)
                {
                    progress.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Exporting,
                        Message = "Downloading file column data..."
                    });

                    fileData = await DownloadFileColumnDataAsync(
                        schema, entityData, options, progress, errors, cancellationToken).ConfigureAwait(false);
                }

                // Write to output file
                progress.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Exporting,
                    Message = "Writing output file..."
                });

                var migrationData = new MigrationData
                {
                    Schema = schema,
                    EntityData = entityData,
                    RelationshipData = relationshipData,
                    FileData = fileData,
                    ExportedAt = DateTime.UtcNow
                };

                await _dataWriter.WriteAsync(migrationData, outputPath, progress, cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();

                var totalRecords = entityResults.Sum(r => r.RecordCount);

                _logger?.LogInformation("Export complete: {Entities} entities, {Records} records in {Duration}",
                    entityResults.Count, totalRecords, stopwatch.Elapsed);

                var result = new ExportResult
                {
                    Success = errors.Count == 0,
                    EntitiesExported = entityResults.Count(r => r.Success),
                    RecordsExported = totalRecords,
                    Duration = stopwatch.Elapsed,
                    EntityResults = entityResults.ToArray(),
                    OutputPath = outputPath,
                    Errors = errors.ToArray(),
                    Warnings = warnings.GetWarnings()
                };

                progress.Complete(new MigrationResult
                {
                    Success = result.Success,
                    RecordsProcessed = result.RecordsExported,
                    SuccessCount = result.RecordsExported,
                    FailureCount = errors.Count,
                    Duration = result.Duration
                });

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                _logger?.LogError(ex, "Export failed");

                var safeMessage = ConnectionStringRedactor.RedactExceptionMessage(ex.Message);
                progress.Error(ex, "Export failed");

                return new ExportResult
                {
                    Success = false,
                    Duration = stopwatch.Elapsed,
                    EntityResults = entityResults.ToArray(),
                    Errors = new[]
                    {
                        new MigrationError
                        {
                            Phase = MigrationPhase.Exporting,
                            Message = safeMessage
                        }
                    },
                    Warnings = warnings.GetWarnings()
                };
            }
        }
        private async Task<EntityExportResultWithData> ExportEntityAsync(
            EntitySchema entitySchema,
            ExportOptions options,
            IProgressReporter progress,
            long recordCount,
            CancellationToken cancellationToken)
        {
            // Warn when schema has a <filter> element but it contains no conditions
            if (!string.IsNullOrWhiteSpace(entitySchema.FetchXmlFilter))
            {
                var summary = SummarizeFilter(entitySchema.FetchXmlFilter);
                if (summary == NoConditionsSummary)
                {
                    progress.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Exporting,
                        Entity = entitySchema.LogicalName,
                        Message = $"Warning: {entitySchema.LogicalName} has a <filter> element in the schema but it contains no conditions — all records will be exported. Check the schema for a malformed filter."
                    });
                }
            }
            else if (entitySchema.FetchXmlFilter != null)
            {
                // FetchXmlFilter is empty/whitespace — <filter> element existed but was empty
                progress.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Exporting,
                    Entity = entitySchema.LogicalName,
                    Message = $"Warning: {entitySchema.LogicalName} has a <filter> element in the schema but it is empty — all records will be exported (no filter applied). Check the schema for a malformed filter."
                });
            }

            var partitionCount = DeterminePartitionCount(recordCount, options);

            if (partitionCount > 1)
            {
                return await ExportEntityPartitionedAsync(
                    entitySchema, partitionCount, recordCount, options, progress, cancellationToken).ConfigureAwait(false);
            }

            return await ExportEntitySequentialAsync(
                entitySchema, options, progress, cancellationToken).ConfigureAwait(false);
        }

        private async Task<EntityExportResultWithData> ExportEntitySequentialAsync(
            EntitySchema entitySchema,
            ExportOptions options,
            IProgressReporter progress,
            CancellationToken cancellationToken)
        {
            var entityStopwatch = Stopwatch.StartNew();
            var records = new List<Entity>();

            try
            {
                _logger?.LogDebug("Exporting entity {Entity} (sequential)", entitySchema.LogicalName);

                var hasFilter = !string.IsNullOrWhiteSpace(entitySchema.FetchXmlFilter);
                var filterDescription = hasFilter ? SummarizeFilter(entitySchema.FetchXmlFilter!) : null;

                // Build FetchXML
                var fetchXml = BuildFetchXml(entitySchema, options.PageSize);
                var pageNumber = 1;
                string? pagingCookie = null;
                var lastReportedCount = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var pagedFetchXml = AddPaging(fetchXml, pageNumber, pagingCookie);

                    // E1: Acquire client per page, release before next iteration's await.
                    // CLAUDE.md NEVER: "Hold single pooled client for multiple queries."
                    EntityCollection response;
                    await using (var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false))
                    {
                        response = await client.RetrieveMultipleAsync(new FetchExpression(pagedFetchXml)).ConfigureAwait(false);
                    }

                    records.AddRange(response.Entities);

                    // Report progress at intervals
                    if (records.Count - lastReportedCount >= options.ProgressInterval || !response.MoreRecords)
                    {
                        var rps = entityStopwatch.Elapsed.TotalSeconds > 0
                            ? records.Count / entityStopwatch.Elapsed.TotalSeconds
                            : 0;

                        progress.Report(new ProgressEventArgs
                        {
                            Phase = MigrationPhase.Exporting,
                            Entity = entitySchema.LogicalName,
                            Current = records.Count,
                            Total = records.Count, // We don't know total upfront
                            RecordsPerSecond = rps,
                            FilterApplied = hasFilter,
                            FilterDescription = filterDescription
                        });

                        lastReportedCount = records.Count;
                    }

                    if (!response.MoreRecords)
                    {
                        break;
                    }

                    pagingCookie = response.PagingCookie;
                    pageNumber++;
                }

                entityStopwatch.Stop();

                _logger?.LogDebug("Exported {Count} records from {Entity} in {Duration}",
                    records.Count, entitySchema.LogicalName, entityStopwatch.Elapsed);

                return new EntityExportResultWithData
                {
                    EntityLogicalName = entitySchema.LogicalName,
                    RecordCount = records.Count,
                    Duration = entityStopwatch.Elapsed,
                    Success = true,
                    Records = records
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entityStopwatch.Stop();

                var safeMessage = ConnectionStringRedactor.RedactExceptionMessage(ex.Message);
                _logger?.LogError(ex, "Failed to export entity {Entity}", entitySchema.LogicalName);

                return new EntityExportResultWithData
                {
                    EntityLogicalName = entitySchema.LogicalName,
                    RecordCount = records.Count,
                    Duration = entityStopwatch.Elapsed,
                    Success = false,
                    ErrorMessage = safeMessage,
                    Records = null
                };
            }
        }

        private async Task<EntityExportResultWithData> ExportEntityPartitionedAsync(
            EntitySchema entitySchema,
            int partitionCount,
            long recordCount,
            ExportOptions options,
            IProgressReporter? progress,
            CancellationToken cancellationToken)
        {
            var entityStopwatch = Stopwatch.StartNew();
            var allRecords = new ConcurrentBag<Entity>();
            long totalExported = 0;

            try
            {
                _logger?.LogInformation("Exporting entity {Entity} with {Partitions} partitions (page-level parallelism)",
                    entitySchema.LogicalName, partitionCount);

                var hasFilter = !string.IsNullOrWhiteSpace(entitySchema.FetchXmlFilter);
                var filterDescription = hasFilter ? SummarizeFilter(entitySchema.FetchXmlFilter!) : null;

                var partitions = GuidPartitioner.CreatePartitions(partitionCount);
                var fetchXml = BuildFetchXml(entitySchema, options.PageSize);

                await Parallel.ForEachAsync(
                    partitions,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = partitionCount,
                        CancellationToken = cancellationToken
                    },
                    async (partition, ct) =>
                    {
                        var partitionRecords = await ExportPartitionAsync(
                            entitySchema, fetchXml, partition, options, ct).ConfigureAwait(false);

                        foreach (var record in partitionRecords)
                        {
                            allRecords.Add(record);
                        }

                        var currentTotal = Interlocked.Add(ref totalExported, partitionRecords.Count);

                        var rps = entityStopwatch.Elapsed.TotalSeconds > 0
                            ? currentTotal / entityStopwatch.Elapsed.TotalSeconds
                            : 0;

                        progress?.Report(new ProgressEventArgs
                        {
                            Phase = MigrationPhase.Exporting,
                            Entity = entitySchema.LogicalName,
                            Current = (int)currentTotal,
                            Total = (int)(recordCount > 0 ? Math.Max(recordCount, currentTotal) : currentTotal),
                            RecordsPerSecond = rps,
                            FilterApplied = hasFilter,
                            FilterDescription = filterDescription
                        });
                    }).ConfigureAwait(false);

                entityStopwatch.Stop();

                var records = allRecords.ToList();

                _logger?.LogDebug("Exported {Count} records from {Entity} in {Duration} ({Partitions} partitions)",
                    records.Count, entitySchema.LogicalName, entityStopwatch.Elapsed, partitionCount);

                return new EntityExportResultWithData
                {
                    EntityLogicalName = entitySchema.LogicalName,
                    RecordCount = records.Count,
                    Duration = entityStopwatch.Elapsed,
                    Success = true,
                    Records = records
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entityStopwatch.Stop();

                var safeMessage = ConnectionStringRedactor.RedactExceptionMessage(ex.Message);
                _logger?.LogError(ex, "Failed to export entity {Entity} (partitioned)", entitySchema.LogicalName);

                return new EntityExportResultWithData
                {
                    EntityLogicalName = entitySchema.LogicalName,
                    RecordCount = allRecords.Count,
                    Duration = entityStopwatch.Elapsed,
                    Success = false,
                    ErrorMessage = safeMessage,
                    Records = null
                };
            }
        }

        private async Task<List<Entity>> ExportPartitionAsync(
            EntitySchema entitySchema,
            string baseFetchXml,
            GuidRange partition,
            ExportOptions options,
            CancellationToken cancellationToken)
        {
            var records = new List<Entity>();

            var fetchXml = AddPartitionFilter(baseFetchXml, entitySchema.PrimaryIdField, partition);
            var pageNumber = 1;
            string? pagingCookie = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pagedFetchXml = AddPaging(fetchXml, pageNumber, pagingCookie);

                // E2: Acquire client per page, release before next iteration's await.
                // CLAUDE.md NEVER: "Hold single pooled client for multiple queries."
                EntityCollection response;
                await using (var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    response = await client.RetrieveMultipleAsync(new FetchExpression(pagedFetchXml)).ConfigureAwait(false);
                }

                records.AddRange(response.Entities);

                if (!response.MoreRecords)
                {
                    break;
                }

                pagingCookie = response.PagingCookie;
                pageNumber++;
            }

            return records;
        }

        private async Task<IReadOnlyDictionary<string, IReadOnlyList<ManyToManyRelationshipData>>> ExportM2MRelationshipsAsync(
            MigrationSchema schema,
            ConcurrentDictionary<string, IReadOnlyList<Entity>> entityData,
            ExportOptions options,
            IProgressReporter progress,
            ConcurrentBag<MigrationError> errors,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entitySchema in schema.Entities)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var m2mRelationships = entitySchema.Relationships.Where(r => r.IsManyToMany).ToList();
                if (m2mRelationships.Count == 0)
                {
                    continue;
                }

                // Only export M2M for records we actually exported
                if (!entityData.TryGetValue(entitySchema.LogicalName, out var exportedRecords) || exportedRecords.Count == 0)
                {
                    continue;
                }

                var exportedIds = exportedRecords.Select(r => r.Id).ToHashSet();
                var entityM2MData = new List<ManyToManyRelationshipData>();

                foreach (var rel in m2mRelationships)
                {
                    // Report message-only (no Entity) to avoid 0/0 display
                    // Entity progress is reported inside ExportM2MRelationshipAsync with actual counts
                    progress.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Exporting,
                        Message = $"Exporting {entitySchema.LogicalName} M2M {rel.Name}..."
                    });

                    try
                    {
                        var relData = await ExportM2MRelationshipAsync(
                            entitySchema, rel, exportedIds, options, progress, cancellationToken).ConfigureAwait(false);
                        entityM2MData.AddRange(relData);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogWarning(ex, "Failed to export M2M relationship {Relationship} for entity {Entity}",
                            rel.Name, entitySchema.LogicalName);
                        errors.Add(new MigrationError
                        {
                            Phase = MigrationPhase.Exporting,
                            EntityLogicalName = entitySchema.LogicalName,
                            Message = $"M2M relationship '{rel.Name}' export failed: {ex.Message}"
                        });
                    }
                }

                if (entityM2MData.Count > 0)
                {
                    result[entitySchema.LogicalName] = entityM2MData;
                    _logger?.LogDebug("Exported {Count} M2M relationship groups for entity {Entity}",
                        entityM2MData.Count, entitySchema.LogicalName);
                }
            }

            return result;
        }

        private async Task<List<ManyToManyRelationshipData>> ExportM2MRelationshipAsync(
            EntitySchema entitySchema,
            RelationshipSchema rel,
            HashSet<Guid> exportedSourceIds,
            ExportOptions options,
            IProgressReporter progress,
            CancellationToken cancellationToken)
        {
            // Query intersect entity to get all associations
            var intersectEntity = rel.IntersectEntity ?? rel.Name;
            var sourceIdField = $"{entitySchema.LogicalName}id";
            var targetIdField = rel.TargetEntityPrimaryKey ?? $"{rel.Entity2}id";

            // C4: validate and escape all values interpolated into FetchXML to prevent injection.
            ValidateLogicalName(intersectEntity, nameof(intersectEntity));
            ValidateLogicalName(sourceIdField, nameof(sourceIdField));
            ValidateLogicalName(targetIdField, nameof(targetIdField));

            var escapedIntersect = SecurityElement.Escape(intersectEntity);
            var escapedSourceIdField = SecurityElement.Escape(sourceIdField);
            var escapedTargetIdField = SecurityElement.Escape(targetIdField);

            // Build FetchXML to query intersect entity
            var fetchXml = $@"<fetch>
                <entity name='{escapedIntersect}'>
                    <attribute name='{escapedSourceIdField}' />
                    <attribute name='{escapedTargetIdField}' />
                </entity>
            </fetch>";

            var pageNumber = 1;
            string? pagingCookie = null;
            var associations = new List<(Guid SourceId, Guid TargetId)>();
            var lastReportedCount = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pagedFetchXml = AddPaging(fetchXml, pageNumber, pagingCookie);

                // E3: Acquire client per page, release before next iteration's await.
                // CLAUDE.md NEVER: "Hold single pooled client for multiple queries."
                EntityCollection response;
                await using (var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    response = await client.RetrieveMultipleAsync(new FetchExpression(pagedFetchXml)).ConfigureAwait(false);
                }

                // Only include associations where both fields exist and source was exported
                var validAssociations = response.Entities
                    .Where(entity => entity.Contains(sourceIdField) && entity.Contains(targetIdField))
                    .Select(entity => (
                        SourceId: entity.GetAttributeValue<Guid>(sourceIdField),
                        TargetId: entity.GetAttributeValue<Guid>(targetIdField)))
                    .Where(assoc => exportedSourceIds.Contains(assoc.SourceId));

                associations.AddRange(validAssociations);

                // Report progress at intervals
                if (associations.Count - lastReportedCount >= options.ProgressInterval || !response.MoreRecords)
                {
                    progress.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Exporting,
                        Entity = entitySchema.LogicalName,
                        Relationship = rel.Name,
                        Current = associations.Count,
                        Total = associations.Count // We don't know total upfront
                    });
                    lastReportedCount = associations.Count;
                }

                if (!response.MoreRecords)
                {
                    break;
                }

                pagingCookie = response.PagingCookie;
                pageNumber++;
            }

            // Group by source ID (CMT format requirement)
            var grouped = associations
                .GroupBy(x => x.SourceId)
                .Select(g => new ManyToManyRelationshipData
                {
                    RelationshipName = rel.Name,
                    SourceEntityName = entitySchema.LogicalName,
                    SourceId = g.Key,
                    TargetEntityName = rel.Entity2,
                    TargetEntityPrimaryKey = targetIdField,
                    TargetIds = g.Select(x => x.TargetId).ToList()
                })
                .ToList();

            return grouped;
        }

        private async Task<Dictionary<string, IReadOnlyList<FileColumnData>>> DownloadFileColumnDataAsync(
            MigrationSchema schema,
            ConcurrentDictionary<string, IReadOnlyList<Entity>> entityData,
            ExportOptions options,
            IProgressReporter progress,
            ConcurrentBag<MigrationError> errors,
            CancellationToken cancellationToken)
        {
            var result = new ConcurrentDictionary<string, IReadOnlyList<FileColumnData>>(StringComparer.OrdinalIgnoreCase);
            var downloadedFiles = 0;

            foreach (var entitySchema in schema.Entities)
            {
                var fileColumns = entitySchema.Fields.Where(f => f.IsFileColumn).ToList();
                if (fileColumns.Count == 0)
                {
                    continue;
                }

                if (!entityData.TryGetValue(entitySchema.LogicalName, out var records) || records.Count == 0)
                {
                    continue;
                }

                var entityFileData = new ConcurrentBag<FileColumnData>();

                await Parallel.ForEachAsync(
                    records,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.DegreeOfParallelism,
                        CancellationToken = cancellationToken
                    },
                    async (record, ct) =>
                    {
                        foreach (var fileColumn in fileColumns)
                        {
                            ct.ThrowIfCancellationRequested();

                            try
                            {
                                var data = await _fileTransferHelper!.DownloadAsync(
                                    entitySchema.LogicalName, record.Id, fileColumn.LogicalName, ct).ConfigureAwait(false);

                                if (data.Length > 0)
                                {
                                    var filePath = $"files/{entitySchema.LogicalName}/{record.Id}_{fileColumn.LogicalName}.bin";

                                    // Retrieve filename/mimetype from the download response metadata
                                    // For now, use field name and generic type - Dataverse doesn't return these in download
                                    var fileName = $"{fileColumn.LogicalName}.bin";
                                    var mimeType = "application/octet-stream";

                                    entityFileData.Add(new FileColumnData
                                    {
                                        RecordId = record.Id,
                                        FieldName = fileColumn.LogicalName,
                                        FileName = fileName,
                                        MimeType = mimeType,
                                        Data = data
                                    });

                                    // Set FileColumnValue marker in entity attributes for CmtDataWriter
                                    record[fileColumn.LogicalName] = new FileColumnValue
                                    {
                                        FilePath = filePath,
                                        FileName = fileName,
                                        MimeType = mimeType
                                    };

                                    Interlocked.Increment(ref downloadedFiles);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger?.LogWarning(ex, "Failed to download file column {Field} for {Entity}/{Record}",
                                    fileColumn.LogicalName, entitySchema.LogicalName, record.Id);
                                errors.Add(new MigrationError
                                {
                                    Phase = MigrationPhase.Exporting,
                                    EntityLogicalName = entitySchema.LogicalName,
                                    RecordId = record.Id,
                                    Message = $"File column '{fileColumn.LogicalName}' download failed: {ex.Message}"
                                });
                            }
                        }
                    }).ConfigureAwait(false);

                if (!entityFileData.IsEmpty)
                {
                    result[entitySchema.LogicalName] = entityFileData.ToList();

                    progress.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Exporting,
                        Entity = entitySchema.LogicalName,
                        Message = $"Downloaded {entityFileData.Count} file(s) for {entitySchema.LogicalName}"
                    });
                }
            }

            _logger?.LogInformation("Downloaded {Count} file column data entries", downloadedFiles);
            return new Dictionary<string, IReadOnlyList<FileColumnData>>(result, StringComparer.OrdinalIgnoreCase);
        }

        private async Task<Dictionary<string, long>> GetEntityRecordCountsAsync(
            IReadOnlyList<EntitySchema> entities,
            ConcurrentBag<string> countFailures,
            CancellationToken cancellationToken)
        {
            var counts = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            await Parallel.ForEachAsync(
                entities,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
                    CancellationToken = cancellationToken
                },
                async (entity, ct) =>
                {
                    try
                    {
                        // C4: validate and escape values before interpolating into FetchXML.
                        ValidateLogicalName(entity.LogicalName, nameof(entity.LogicalName));
                        ValidateLogicalName(entity.PrimaryIdField, nameof(entity.PrimaryIdField));
                        var escapedEntity = SecurityElement.Escape(entity.LogicalName);
                        var escapedPk = SecurityElement.Escape(entity.PrimaryIdField);

                        // Acquire and release per query to comply with pool discipline.
                        await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: ct).ConfigureAwait(false);

                        var fetchXml = $@"<fetch aggregate=""true""><entity name=""{escapedEntity}""><attribute name=""{escapedPk}"" alias=""cnt"" aggregate=""count""/></entity></fetch>";

                        var response = await client.RetrieveMultipleAsync(new FetchExpression(fetchXml)).ConfigureAwait(false);
                        var aliased = response.Entities.FirstOrDefault()?.GetAttributeValue<AliasedValue>("cnt");

                        if (aliased?.Value != null)
                        {
                            counts[entity.LogicalName] = Convert.ToInt64(aliased.Value);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // F1: surface at Warning level (not Debug) and record the failure so the
                        // caller can emit a WarningCollector entry. Entity will fall back to
                        // sequential export because recordCount=0 drops it below the partition threshold.
                        _logger?.LogWarning(ex,
                            "Failed to get record count for {Entity} — falling back to sequential export",
                            entity.LogicalName);
                        countFailures.Add(entity.LogicalName);
                    }
                }).ConfigureAwait(false);

            return new Dictionary<string, long>(counts, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates that a Dataverse logical name matches the expected shape.
        /// Backstop guard against malicious or malformed names being interpolated into FetchXML.
        /// </summary>
        private static readonly Regex LogicalNamePattern = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

        private static void ValidateLogicalName(string value, string argumentName)
        {
            if (string.IsNullOrEmpty(value) || !LogicalNamePattern.IsMatch(value))
            {
                throw new ArgumentException(
                    $"Invalid Dataverse logical name '{value}'. Must match ^[a-z][a-z0-9_]*$.",
                    argumentName);
            }
        }

        /// <summary>
        /// Determines the number of GUID range partitions to use for a single entity export.
        /// </summary>
        /// <param name="recordCount">Approximate record count for the entity.</param>
        /// <param name="options">Export options controlling parallelism.</param>
        /// <returns>1 for sequential export, or N for partitioned export.</returns>
        public static int DeterminePartitionCount(long recordCount, ExportOptions options)
        {
            if (options.PageLevelParallelism == 1)
                return 1;

            // Explicit partition count always honored (user override)
            if (options.PageLevelParallelism > 1)
                return options.PageLevelParallelism;

            // Auto mode: only partition above threshold
            if (options.PageLevelParallelismThreshold <= 0 || recordCount <= options.PageLevelParallelismThreshold)
                return 1;

            // Auto: scale partitions with entity size, capped at 16
            var auto = (int)(recordCount / options.PageLevelParallelismThreshold);
            return Math.Min(16, Math.Max(2, auto));
        }

        /// <summary>
        /// Adds GUID range filter conditions to FetchXML for partition-based export.
        /// </summary>
        /// <param name="fetchXml">Base FetchXML query.</param>
        /// <param name="primaryKeyField">Primary key field name for the entity.</param>
        /// <param name="partition">GUID range to filter by.</param>
        /// <returns>Modified FetchXML with partition filter conditions.</returns>
        public static string AddPartitionFilter(string fetchXml, string primaryKeyField, GuidRange partition)
        {
            if (partition.IsFull)
                return fetchXml;

            var doc = XDocument.Parse(fetchXml);
            var entity = doc.Root!.Element("entity")!;

            var filter = new XElement("filter", new XAttribute("type", "and"));

            if (partition.LowerBound.HasValue)
            {
                filter.Add(new XElement("condition",
                    new XAttribute("attribute", primaryKeyField),
                    new XAttribute("operator", "ge"),
                    new XAttribute("value", partition.LowerBound.Value.ToString())));
            }

            if (partition.UpperBound.HasValue)
            {
                filter.Add(new XElement("condition",
                    new XAttribute("attribute", primaryKeyField),
                    new XAttribute("operator", "lt"),
                    new XAttribute("value", partition.UpperBound.Value.ToString())));
            }

            entity.Add(filter);

            return doc.ToString(SaveOptions.DisableFormatting);
        }

        private string BuildFetchXml(EntitySchema entitySchema, int pageSize)
        {
            var fetch = new XElement("fetch",
                new XAttribute("count", pageSize),
                new XAttribute("returntotalrecordcount", "false"),
                new XElement("entity",
                    new XAttribute("name", entitySchema.LogicalName),
                    entitySchema.Fields.Select(f => new XElement("attribute",
                        new XAttribute("name", f.LogicalName)))));

            // Add filter if specified
            if (!string.IsNullOrEmpty(entitySchema.FetchXmlFilter))
            {
                var filterDoc = XDocument.Parse($"<root>{entitySchema.FetchXmlFilter}</root>");
                var entityElement = fetch.Element("entity");
                if (filterDoc.Root != null)
                {
                    foreach (var child in filterDoc.Root.Elements())
                    {
                        entityElement?.Add(child);
                    }
                }
            }

            return fetch.ToString(SaveOptions.DisableFormatting);
        }

        internal const string NoConditionsSummary = "(filter — no conditions)";

        /// <summary>
        /// Summarizes a FetchXML filter into a human-readable description.
        /// Extracts attribute/operator/value from condition elements.
        /// Uses neutral separator since filters may mix AND/OR logic.
        /// </summary>
        public static string SummarizeFilter(string fetchXmlFilter)
        {
            try
            {
                var doc = XDocument.Parse($"<root>{fetchXmlFilter}</root>");
                var conditions = doc.Descendants("condition").ToList();

                if (conditions.Count == 0)
                    return NoConditionsSummary;

                var parts = conditions.Select(c =>
                {
                    var attr = c.Attribute("attribute")?.Value ?? "?";
                    var op = c.Attribute("operator")?.Value ?? "?";
                    var val = c.Attribute("value")?.Value;

                    // Check for child <value> elements (used by 'in', 'not-in' operators)
                    if (val == null)
                    {
                        var childValues = c.Elements("value").Select(v => v.Value).ToList();
                        if (childValues.Count == 1) val = childValues[0];
                        else if (childValues.Count > 1) val = string.Join(",", childValues);
                    }

                    return val != null ? $"{attr} {op} '{val}'" : $"{attr} {op}";
                }).ToList();

                // Cap at 3 conditions to keep output concise
                var summary = parts.Count <= 3
                    ? string.Join(", ", parts)
                    : string.Join(", ", parts.Take(3)) + $" (+{parts.Count - 3} more)";

                return summary;
            }
            catch (Exception)
            {
                // Best-effort summary — malformed XML should not break export
                return "(filter)";
            }
        }

        private string AddPaging(string fetchXml, int pageNumber, string? pagingCookie)
        {
            var doc = XDocument.Parse(fetchXml);
            var fetch = doc.Root!;

            fetch.SetAttributeValue("page", pageNumber);

            if (!string.IsNullOrEmpty(pagingCookie))
            {
                fetch.SetAttributeValue("paging-cookie", pagingCookie);
            }

            return doc.ToString(SaveOptions.DisableFormatting);
        }

        private class EntityExportResultWithData : EntityExportResult
        {
            public IReadOnlyList<Entity>? Records { get; set; }
        }
    }

    /// <summary>
    /// Standard warning codes for export operations.
    /// </summary>
    public static class ExportWarningCodes
    {
        /// <summary>Record count query failed, entity fell back to sequential export.</summary>
        public const string CountFailedSequentialFallback = "COUNT_FAILED_SEQUENTIAL_FALLBACK";
    }
}
