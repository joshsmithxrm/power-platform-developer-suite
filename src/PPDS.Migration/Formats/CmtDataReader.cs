using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Formats
{
    /// <summary>
    /// Reads CMT data.zip files.
    /// </summary>
    public class CmtDataReader : ICmtDataReader
    {
        private readonly ICmtSchemaReader _schemaReader;
        private readonly ILogger<CmtDataReader>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="CmtDataReader"/> class.
        /// </summary>
        /// <param name="schemaReader">The schema reader.</param>
        public CmtDataReader(ICmtSchemaReader schemaReader)
        {
            _schemaReader = schemaReader ?? throw new ArgumentNullException(nameof(schemaReader));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CmtDataReader"/> class.
        /// </summary>
        /// <param name="schemaReader">The schema reader.</param>
        /// <param name="logger">The logger.</param>
        public CmtDataReader(ICmtSchemaReader schemaReader, ILogger<CmtDataReader> logger)
            : this(schemaReader)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<MigrationData> ReadAsync(string path, IProgressReporter? progress = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Data file not found: {path}", path);
            }

            _logger?.LogInformation("Reading data from {Path}", path);

#if NET8_0_OR_GREATER
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
#else
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
#endif
            return await ReadAsync(stream, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<MigrationData> ReadAsync(Stream stream, IProgressReporter? progress = null, CancellationToken cancellationToken = default)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            progress ??= IProgressReporter.Silent;

            progress.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = "Opening data archive..."
            });

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            // Read schema from archive
            var schemaEntry = archive.GetEntry("data_schema.xml") ?? archive.GetEntry("schema.xml");
            MigrationSchema schema;

            if (schemaEntry != null)
            {
                using var schemaStream = schemaEntry.Open();
                using var memoryStream = new MemoryStream();
                await schemaStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
                memoryStream.Position = 0;
                schema = await _schemaReader.ReadAsync(memoryStream, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException("Data archive does not contain a schema file (data_schema.xml or schema.xml)");
            }

            // Read data from archive
            var dataEntry = archive.GetEntry("data.xml") ?? throw new InvalidOperationException("Data archive does not contain data.xml");

            using var dataStream = dataEntry.Open();
            using var dataMemoryStream = new MemoryStream();
            await dataStream.CopyToAsync(dataMemoryStream, cancellationToken).ConfigureAwait(false);
            dataMemoryStream.Position = 0;

            var (entityData, relationshipData) = await ParseDataXmlAsync(dataMemoryStream, schema, progress, cancellationToken).ConfigureAwait(false);

            // Read file column data from files/ directory in ZIP
            var fileData = await ReadFileDataFromArchiveAsync(archive, entityData, cancellationToken).ConfigureAwait(false);

            // Strip FileColumnValue markers from entity attributes so they don't leak
            // into Dataverse requests during import (they were only needed for metadata lookup above)
            StripFileColumnMarkers(entityData);

            _logger?.LogInformation("Parsed data with {RecordCount} total records, {M2MCount} M2M relationship groups, and {FileCount} file column entries",
                entityData.Values.Sum(v => v.Count),
                relationshipData.Values.Sum(v => v.Count),
                fileData.Values.Sum(v => v.Count));

            return new MigrationData
            {
                Schema = schema,
                EntityData = entityData,
                RelationshipData = relationshipData,
                FileData = fileData,
                ExportedAt = DateTime.UtcNow
            };
        }

        private async Task<(IReadOnlyDictionary<string, IReadOnlyList<Entity>>, IReadOnlyDictionary<string, IReadOnlyList<ManyToManyRelationshipData>>)> ParseDataXmlAsync(
            Stream stream,
            MigrationSchema schema,
            IProgressReporter progress,
            CancellationToken cancellationToken)
        {
#if NET8_0_OR_GREATER
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
#else
            var doc = XDocument.Load(stream, LoadOptions.None);
            await Task.CompletedTask;
#endif

            var root = doc.Root ?? throw new InvalidOperationException("Data XML has no root element");
            var entitiesElement = root.Name.LocalName.Equals("entities", StringComparison.OrdinalIgnoreCase)
                ? root
                : root.Element("entities") ?? throw new InvalidOperationException("Data XML has no <entities> element");

            var entityResult = new Dictionary<string, IReadOnlyList<Entity>>(StringComparer.OrdinalIgnoreCase);
            var relationshipResult = new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entityElement in entitiesElement.Elements("entity"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entityName = entityElement.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(entityName))
                {
                    continue;
                }

                var entitySchema = schema.GetEntity(entityName);
                var records = new List<Entity>();

                foreach (var recordElement in entityElement.Elements("records").Elements("record"))
                {
                    var record = ParseRecord(recordElement, entityName, entitySchema);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }

                if (records.Count > 0)
                {
                    entityResult[entityName] = records;
                    _logger?.LogDebug("Parsed {Count} records for entity {Entity}", records.Count, entityName);
                }

                // Parse M2M relationships
                var m2mElement = entityElement.Element("m2mrelationships");
                if (m2mElement != null)
                {
                    var m2mData = ParseM2MRelationships(m2mElement, entityName);
                    if (m2mData.Count > 0)
                    {
                        relationshipResult[entityName] = m2mData;
                        _logger?.LogDebug("Parsed {Count} M2M relationship groups for entity {Entity}", m2mData.Count, entityName);
                    }
                }
            }

            return (entityResult, relationshipResult);
        }

        private List<ManyToManyRelationshipData> ParseM2MRelationships(XElement element, string sourceEntityName)
        {
            var result = new List<ManyToManyRelationshipData>();

            foreach (var m2mRel in element.Elements("m2mrelationship"))
            {
                var sourceId = m2mRel.Attribute("sourceid")?.Value;
                var targetEntityName = m2mRel.Attribute("targetentityname")?.Value;
                var targetEntityPrimaryKey = m2mRel.Attribute("targetentitynameidfield")?.Value;
                var relationshipName = m2mRel.Attribute("m2mrelationshipname")?.Value;

                if (string.IsNullOrEmpty(sourceId) || !Guid.TryParse(sourceId, out var sourceGuid))
                {
                    continue;
                }

                var targetIdsElement = m2mRel.Element("targetids");
                var targetIds = targetIdsElement?.Elements("targetid")
                    .Select(e => Guid.TryParse(e.Value, out var g) ? g : (Guid?)null)
                    .Where(g => g.HasValue)
                    .Select(g => g!.Value)
                    .ToList() ?? new List<Guid>();

                if (targetIds.Count > 0)
                {
                    result.Add(new ManyToManyRelationshipData
                    {
                        RelationshipName = relationshipName ?? string.Empty,
                        SourceEntityName = sourceEntityName,
                        SourceId = sourceGuid,
                        TargetEntityName = targetEntityName ?? string.Empty,
                        TargetEntityPrimaryKey = targetEntityPrimaryKey ?? string.Empty,
                        TargetIds = targetIds
                    });
                }
            }

            return result;
        }

        private Entity? ParseRecord(XElement recordElement, string entityName, EntitySchema? entitySchema)
        {
            var idAttribute = recordElement.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(idAttribute) || !Guid.TryParse(idAttribute, out var recordId))
            {
                return null;
            }

            var entity = new Entity(entityName, recordId);

            foreach (var fieldElement in recordElement.Elements("field"))
            {
                var fieldName = fieldElement.Attribute("name")?.Value;
                var fieldValue = fieldElement.Attribute("value")?.Value ?? fieldElement.Value;
                var fieldType = fieldElement.Attribute("type")?.Value;

                if (string.IsNullOrEmpty(fieldName))
                {
                    continue;
                }

                var schemaField = entitySchema?.Fields.FirstOrDefault(f =>
                    f.LogicalName.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

                var parsedValue = ParseFieldValue(fieldValue, fieldType ?? schemaField?.Type, fieldElement);
                if (parsedValue != null)
                {
                    entity[fieldName] = parsedValue;
                }
            }

            return entity;
        }

        private object? ParseFieldValue(string? value, string? type, XElement element)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            // Infer lookup type from lookupentity attribute when type is missing
            if (string.IsNullOrEmpty(type) && element.Attribute("lookupentity") != null)
            {
                type = "lookup";
            }

            type = type?.ToLowerInvariant();

            return type switch
            {
                "string" or "memo" or "nvarchar" => value,
                "int" or "integer" or "number" => int.TryParse(value, out var i) ? i : null,
                "bigint" => long.TryParse(value, out var l) ? l : null,
                "decimal" or "money" => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null,
                "float" or "double" => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : null,
                "bool" or "boolean" => value == "1" || (bool.TryParse(value, out var b) && b),
                "datetime" => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null,
                "guid" or "uniqueidentifier" => Guid.TryParse(value, out var g) ? g : null,
                "lookup" or "customer" or "owner" or "entityreference" or "partylist" => ParseEntityReference(element),
                "optionset" or "optionsetvalue" or "picklist" => ParseOptionSetValue(value),
                "state" or "status" => ParseOptionSetValue(value),
                "file" => ParseFileColumnValue(value, element),
                _ => value // Return as string for unknown types
            };
        }

        private EntityReference? ParseEntityReference(XElement element)
        {
            // CMT format: GUID is element content, entity name is lookupentity attribute
            var idValue = element.Value;  // Element content (CMT format)
            if (string.IsNullOrEmpty(idValue))
            {
                // Fallback for other formats
                idValue = element.Attribute("value")?.Value ?? element.Attribute("id")?.Value;
            }

            var entityName = element.Attribute("lookupentity")?.Value;
            var name = element.Attribute("lookupentityname")?.Value;

            if (string.IsNullOrEmpty(idValue) || !Guid.TryParse(idValue, out var id))
            {
                return null;
            }

            if (string.IsNullOrEmpty(entityName))
            {
                return null;
            }

            return new EntityReference(entityName, id) { Name = name };
        }

        private OptionSetValue? ParseOptionSetValue(string? value)
        {
            if (string.IsNullOrEmpty(value) || !int.TryParse(value, out var optionValue))
            {
                return null;
            }

            return new OptionSetValue(optionValue);
        }

        private static void StripFileColumnMarkers(IReadOnlyDictionary<string, IReadOnlyList<Entity>> entityData)
        {
            foreach (var records in entityData.Values)
            {
                foreach (var record in records)
                {
                    var fileColumnKeys = record.Attributes
                        .Where(a => a.Value is FileColumnValue)
                        .Select(a => a.Key)
                        .ToList();

                    foreach (var key in fileColumnKeys)
                    {
                        record.Attributes.Remove(key);
                    }
                }
            }
        }

        private FileColumnValue? ParseFileColumnValue(string? value, XElement element)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return new FileColumnValue
            {
                FilePath = value,
                FileName = element.Attribute("filename")?.Value ?? string.Empty,
                MimeType = element.Attribute("mimetype")?.Value ?? string.Empty
            };
        }

        private async Task<IReadOnlyDictionary<string, IReadOnlyList<FileColumnData>>> ReadFileDataFromArchiveAsync(
            ZipArchive archive,
            IReadOnlyDictionary<string, IReadOnlyList<Entity>> entityData,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, IReadOnlyList<FileColumnData>>(StringComparer.OrdinalIgnoreCase);

            // Find all entries under files/ prefix
            var fileEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (fileEntries.Count == 0)
            {
                return result;
            }

            var fileDataByEntity = new Dictionary<string, List<FileColumnData>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in fileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Expected format: files/{entityname}/{recordid}_{fieldname}.bin
                var parts = entry.FullName.Split('/');
                if (parts.Length != 3)
                {
                    continue;
                }

                var entityName = parts[1];
                var fileName = parts[2];

                // Parse recordid and fieldname from filename: {recordid}_{fieldname}.bin
                var binName = Path.GetFileNameWithoutExtension(fileName);
                var underscoreIndex = binName.IndexOf('_');
                if (underscoreIndex < 0)
                {
                    continue;
                }

                var recordIdStr = binName.Substring(0, underscoreIndex);
                var fieldName = binName.Substring(underscoreIndex + 1);

                if (!Guid.TryParse(recordIdStr, out var recordId))
                {
                    continue;
                }

                // Look up the original filename and mimetype from entity data (FileColumnValue in attributes)
                var originalFileName = string.Empty;
                var mimeType = string.Empty;

                if (entityData.TryGetValue(entityName, out var records))
                {
                    var record = records.FirstOrDefault(r => r.Id == recordId);
                    if (record != null && record.Contains(fieldName) && record[fieldName] is FileColumnValue fcv)
                    {
                        originalFileName = fcv.FileName;
                        mimeType = fcv.MimeType;
                    }
                }

                // Read binary data
                using var entryStream = entry.Open();
                using var memoryStream = new MemoryStream();
                await entryStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);

                if (!fileDataByEntity.ContainsKey(entityName))
                {
                    fileDataByEntity[entityName] = new List<FileColumnData>();
                }

                fileDataByEntity[entityName].Add(new FileColumnData
                {
                    RecordId = recordId,
                    FieldName = fieldName,
                    FileName = originalFileName,
                    MimeType = mimeType,
                    Data = memoryStream.ToArray()
                });
            }

            foreach (var (entityName, list) in fileDataByEntity)
            {
                result[entityName] = list;
            }

            return result;
        }
    }
}
