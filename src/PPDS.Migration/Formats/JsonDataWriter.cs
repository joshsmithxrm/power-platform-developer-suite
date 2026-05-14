using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Formats
{
    /// <summary>
    /// Writes migration data as a PPDS-native JSON document.
    /// </summary>
    /// <remarks>
    /// JSON structure mirrors the CMT XML payload (schema + per-entity records + m2m relationships)
    /// so consumers can build a 1:1 mental model between the two formats.
    /// File-column binary data is not serialized in JSON v1 (single-file output) — when present,
    /// a warning is emitted and binaries are skipped. Round-tripping file columns is out of scope
    /// for v1; use CMT format if file data must round-trip.
    /// </remarks>
    public class JsonDataWriter : IJsonDataWriter
    {
        /// <summary>
        /// Current JSON export format version. Bump on breaking schema changes.
        /// </summary>
        public const string FormatVersion = "1.0";

        /// <summary>
        /// $schema URI for the JSON export document.
        /// </summary>
        public const string SchemaUri = "https://ppds.dev/schemas/data-export/v1.json";

        private readonly ILogger<JsonDataWriter>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonDataWriter"/> class.
        /// </summary>
        public JsonDataWriter()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonDataWriter"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public JsonDataWriter(ILogger<JsonDataWriter> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task WriteAsync(MigrationData data, string path, IProgressReporter? progress = null, CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

            _logger?.LogInformation("Writing JSON export to {Path}", path);

#if NET8_0_OR_GREATER
            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
#else
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
#endif
            await WriteAsync(data, stream, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task WriteAsync(MigrationData data, Stream stream, IProgressReporter? progress = null, CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            progress ??= IProgressReporter.Silent;

            if (data.FileData.Count > 0)
            {
                progress.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Exporting,
                    Message = "Warning: file column binary data is not included in JSON export (use --format cmt for file-column round-trip)."
                });
                _logger?.LogWarning("File column data present but JSON format does not serialize binaries — skipping {Count} entity group(s).", data.FileData.Count);
            }

            var writerOptions = new JsonWriterOptions
            {
                Indented = true,
                SkipValidation = false
            };

            await using var writer = new Utf8JsonWriter(stream, writerOptions);

            writer.WriteStartObject();
            writer.WriteString("$schema", SchemaUri);
            writer.WriteString("formatVersion", FormatVersion);
            writer.WriteString("exportedAt", data.ExportedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(data.SourceEnvironment))
            {
                writer.WriteString("sourceEnvironment", data.SourceEnvironment);
            }

            WriteSchema(writer, data.Schema);
            await WriteEntitiesAsync(writer, data, progress, cancellationToken).ConfigureAwait(false);

            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation("Wrote {RecordCount} total records to JSON", data.TotalRecordCount);
        }

        private static void WriteSchema(Utf8JsonWriter writer, MigrationSchema schema)
        {
            writer.WritePropertyName("schema");
            writer.WriteStartObject();
            writer.WritePropertyName("entities");
            writer.WriteStartArray();

            foreach (var entity in schema.Entities)
            {
                writer.WriteStartObject();
                writer.WriteString("logicalName", entity.LogicalName);
                writer.WriteString("displayName", entity.DisplayName);
                writer.WriteString("primaryIdField", entity.PrimaryIdField);
                writer.WriteString("primaryNameField", entity.PrimaryNameField);
                writer.WriteNumber("objectTypeCode", entity.ObjectTypeCode ?? 0);
                writer.WriteBoolean("disablePlugins", entity.DisablePlugins);

                writer.WritePropertyName("fields");
                writer.WriteStartArray();
                foreach (var field in entity.Fields)
                {
                    writer.WriteStartObject();
                    writer.WriteString("logicalName", field.LogicalName);
                    writer.WriteString("displayName", field.DisplayName);
                    writer.WriteString("type", field.Type);
                    if (field.IsPrimaryKey)
                    {
                        writer.WriteBoolean("isPrimaryKey", true);
                    }
                    if (!string.IsNullOrEmpty(field.LookupEntity))
                    {
                        writer.WriteString("lookupType", field.LookupEntity);
                    }
                    if (field.MaxFileSizeKB.HasValue)
                    {
                        writer.WriteNumber("maxFileSizeKB", field.MaxFileSizeKB.Value);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                if (entity.Relationships.Count > 0)
                {
                    writer.WritePropertyName("relationships");
                    writer.WriteStartArray();
                    foreach (var rel in entity.Relationships)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", rel.Name);
                        writer.WriteBoolean("manyToMany", rel.IsManyToMany);
                        if (rel.IsReflexive)
                        {
                            writer.WriteBoolean("isReflexive", true);
                        }
                        if (rel.IsManyToMany)
                        {
                            if (!string.IsNullOrEmpty(rel.Entity2))
                            {
                                writer.WriteString("m2mTargetEntity", rel.Entity2);
                            }
                            if (!string.IsNullOrEmpty(rel.TargetEntityPrimaryKey))
                            {
                                writer.WriteString("m2mTargetEntityPrimaryKey", rel.TargetEntityPrimaryKey);
                            }
                        }
                        else if (!string.IsNullOrEmpty(rel.Entity2))
                        {
                            writer.WriteString("relatedEntityName", rel.Entity2);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }

                if (!string.IsNullOrEmpty(entity.FetchXmlFilter))
                {
                    writer.WriteString("filter", entity.FetchXmlFilter);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private async Task WriteEntitiesAsync(
            Utf8JsonWriter writer,
            MigrationData data,
            IProgressReporter progress,
            CancellationToken cancellationToken)
        {
            writer.WritePropertyName("entities");
            writer.WriteStartArray();

            foreach (var (entityName, records) in data.EntityData)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entitySchema = data.Schema.Entities.FirstOrDefault(e => e.LogicalName == entityName);
                var displayName = entitySchema?.DisplayName ?? entityName;

                writer.WriteStartObject();
                writer.WriteString("logicalName", entityName);
                writer.WriteString("displayName", displayName);

                writer.WritePropertyName("records");
                writer.WriteStartArray();
                foreach (var record in records)
                {
                    WriteRecord(writer, record);
                }
                writer.WriteEndArray();

                writer.WritePropertyName("m2mRelationships");
                writer.WriteStartArray();
                if (data.RelationshipData.TryGetValue(entityName, out var m2mList))
                {
                    foreach (var m2m in m2mList)
                    {
                        WriteM2MRelationship(writer, m2m);
                    }
                }
                writer.WriteEndArray();

                writer.WriteEndObject();

                if (writer.BytesPending > 65536)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            writer.WriteEndArray();
        }

        private static void WriteRecord(Utf8JsonWriter writer, Entity record)
        {
            writer.WriteStartObject();
            writer.WriteString("id", record.Id.ToString());

            writer.WritePropertyName("fields");
            writer.WriteStartObject();
            foreach (var attribute in record.Attributes)
            {
                WriteField(writer, attribute.Key, attribute.Value);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        private static void WriteField(Utf8JsonWriter writer, string name, object? value)
        {
            if (value == null)
            {
                return;
            }

            writer.WritePropertyName(name);
            writer.WriteStartObject();

            switch (value)
            {
                case EntityReference er:
                    writer.WriteString("value", er.Id.ToString());
                    writer.WriteString("lookupEntity", er.LogicalName);
                    if (!string.IsNullOrEmpty(er.Name))
                    {
                        writer.WriteString("lookupName", er.Name);
                    }
                    break;

                case OptionSetValue osv:
                    writer.WriteNumber("value", osv.Value);
                    break;

                case Money m:
                    writer.WriteNumber("value", m.Value);
                    break;

                case DateTime dt:
                    writer.WriteString(
                        "value",
                        dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                    break;

                case bool b:
                    writer.WriteBoolean("value", b);
                    break;

                case Guid g:
                    writer.WriteString("value", g.ToString());
                    break;

                case int i:
                    writer.WriteNumber("value", i);
                    break;

                case long l:
                    writer.WriteNumber("value", l);
                    break;

                case decimal d:
                    writer.WriteNumber("value", d);
                    break;

                case double dbl:
                    writer.WriteNumber("value", dbl);
                    break;

                case float f:
                    writer.WriteNumber("value", f);
                    break;

                case string s:
                    writer.WriteString("value", s);
                    break;

                case FileColumnValue fcv:
                    writer.WriteString("value", fcv.FilePath);
                    writer.WriteString("fileName", fcv.FileName);
                    writer.WriteString("mimeType", fcv.MimeType);
                    break;

                default:
                    writer.WriteString("value", value.ToString() ?? string.Empty);
                    break;
            }

            writer.WriteEndObject();
        }

        private static void WriteM2MRelationship(Utf8JsonWriter writer, ManyToManyRelationshipData m2m)
        {
            writer.WriteStartObject();
            writer.WriteString("relationshipName", m2m.RelationshipName);
            writer.WriteString("sourceId", m2m.SourceId.ToString());
            writer.WriteString("targetEntityName", m2m.TargetEntityName);
            writer.WriteString("targetEntityPrimaryKey", m2m.TargetEntityPrimaryKey);

            writer.WritePropertyName("targetIds");
            writer.WriteStartArray();
            foreach (var targetId in m2m.TargetIds)
            {
                writer.WriteStringValue(targetId.ToString());
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
    }
}
