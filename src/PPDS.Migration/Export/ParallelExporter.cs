using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        /// <param name="migrationOptions">Migration options from DI.</param>
        /// <param name="logger">The logger.</param>
        public ParallelExporter(
            IDataverseConnectionPool connectionPool,
            ICmtSchemaReader schemaReader,
            ICmtDataWriter dataWriter,
            IOptions<MigrationOptions>? migrationOptions = null,
            ILogger<ParallelExporter>? logger = null)
            : this(connectionPool, schemaReader, dataWriter)
        {
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
            progress?.Report(new ProgressEventArgs
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

            options ??= _defaultOptions;
            var stopwatch = Stopwatch.StartNew();
            var entityResults = new ConcurrentBag<EntityExportResult>();
            var entityData = new ConcurrentDictionary<string, IReadOnlyList<Entity>>(StringComparer.OrdinalIgnoreCase);
            var errors = new ConcurrentBag<MigrationError>();

            _logger?.LogInformation("Starting parallel export of {Count} entities with parallelism {Parallelism}",
                schema.Entities.Count, options.DegreeOfParallelism);

            progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Exporting,
                Message = $"Exporting {schema.Entities.Count} entities..."
            });

            try
            {
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
                        var result = await ExportEntityAsync(entitySchema, options, progress, ct).ConfigureAwait(false);
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
                progress?.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Exporting,
                    Message = "Exporting M2M relationships..."
                });

                var relationshipData = await ExportM2MRelationshipsAsync(
                    schema, entityData, options, progress, cancellationToken).ConfigureAwait(false);

                // Write to output file
                progress?.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Exporting,
                    Message = "Writing output file..."
                });

                var migrationData = new MigrationData
                {
                    Schema = schema,
                    EntityData = entityData,
                    RelationshipData = relationshipData,
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
                    Errors = errors.ToArray()
                };

                progress?.Complete(new MigrationResult
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
                progress?.Error(ex, "Export failed");

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
                    }
                };
            }
        }

        private async Task<EntityExportResultWithData> ExportEntityAsync(
            EntitySchema entitySchema,
            ExportOptions options,
            IProgressReporter? progress,
            CancellationToken cancellationToken)
        {
            var entityStopwatch = Stopwatch.StartNew();
            var records = new List<Entity>();

            try
            {
                _logger?.LogDebug("Exporting entity {Entity}", entitySchema.LogicalName);

                await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

                // Build FetchXML
                var fetchXml = BuildFetchXml(entitySchema, options.PageSize);
                var pageNumber = 1;
                string? pagingCookie = null;
                var lastReportedCount = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var pagedFetchXml = AddPaging(fetchXml, pageNumber, pagingCookie);
                    var response = await client.RetrieveMultipleAsync(new FetchExpression(pagedFetchXml)).ConfigureAwait(false);

                    records.AddRange(response.Entities);

                    // Report progress at intervals
                    if (records.Count - lastReportedCount >= options.ProgressInterval || !response.MoreRecords)
                    {
                        var rps = entityStopwatch.Elapsed.TotalSeconds > 0
                            ? records.Count / entityStopwatch.Elapsed.TotalSeconds
                            : 0;

                        progress?.Report(new ProgressEventArgs
                        {
                            Phase = MigrationPhase.Exporting,
                            Entity = entitySchema.LogicalName,
                            Current = records.Count,
                            Total = records.Count, // We don't know total upfront
                            RecordsPerSecond = rps
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

        private async Task<IReadOnlyDictionary<string, IReadOnlyList<ManyToManyRelationshipData>>> ExportM2MRelationshipsAsync(
            MigrationSchema schema,
            ConcurrentDictionary<string, IReadOnlyList<Entity>> entityData,
            ExportOptions options,
            IProgressReporter? progress,
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
                    progress?.Report(new ProgressEventArgs
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
            IProgressReporter? progress,
            CancellationToken cancellationToken)
        {
            await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Query intersect entity to get all associations
            var intersectEntity = rel.IntersectEntity ?? rel.Name;
            var sourceIdField = $"{entitySchema.LogicalName}id";
            var targetIdField = rel.TargetEntityPrimaryKey ?? $"{rel.Entity2}id";

            // Build FetchXML to query intersect entity
            var fetchXml = $@"<fetch>
                <entity name='{intersectEntity}'>
                    <attribute name='{sourceIdField}' />
                    <attribute name='{targetIdField}' />
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
                var response = await client.RetrieveMultipleAsync(new FetchExpression(pagedFetchXml)).ConfigureAwait(false);

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
                    progress?.Report(new ProgressEventArgs
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
}
