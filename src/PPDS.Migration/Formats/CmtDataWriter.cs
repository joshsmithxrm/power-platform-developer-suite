using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Formats
{
    /// <summary>
    /// Writes CMT-compatible data.zip files.
    /// </summary>
    public class CmtDataWriter : ICmtDataWriter
    {
        private readonly ILogger<CmtDataWriter>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="CmtDataWriter"/> class.
        /// </summary>
        public CmtDataWriter()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CmtDataWriter"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public CmtDataWriter(ILogger<CmtDataWriter> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task WriteAsync(MigrationData data, string path, IProgressReporter? progress = null, CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

            _logger?.LogInformation("Writing data to {Path}", path);

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

            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

            // Write [Content_Types].xml (required by CMT)
            var contentTypesEntry = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
            using (var contentTypesStream = contentTypesEntry.Open())
            {
                await WriteContentTypesAsync(contentTypesStream).ConfigureAwait(false);
            }

            // Write data.xml
            progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Exporting,
                Message = "Writing data.xml..."
            });

            var dataEntry = archive.CreateEntry("data.xml", CompressionLevel.Optimal);
            using (var dataStream = dataEntry.Open())
            {
                await WriteDataXmlAsync(data, dataStream, progress, cancellationToken).ConfigureAwait(false);
            }

            // Write schema
            progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Exporting,
                Message = "Writing data_schema.xml..."
            });

            var schemaEntry = archive.CreateEntry("data_schema.xml", CompressionLevel.Optimal);
            using (var schemaStream = schemaEntry.Open())
            {
                await WriteSchemaXmlAsync(data.Schema, schemaStream, cancellationToken).ConfigureAwait(false);
            }

            _logger?.LogInformation("Wrote {RecordCount} total records", data.TotalRecordCount);
        }

        private static async Task WriteContentTypesAsync(Stream stream)
        {
            var settings = new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                Encoding = new UTF8Encoding(false)
            };

#if NET8_0_OR_GREATER
            await using var writer = XmlWriter.Create(stream, settings);
#else
            using var writer = XmlWriter.Create(stream, settings);
#endif

            await writer.WriteStartDocumentAsync().ConfigureAwait(false);
            await writer.WriteStartElementAsync(null, "Types", "http://schemas.openxmlformats.org/package/2006/content-types").ConfigureAwait(false);
            await writer.WriteStartElementAsync(null, "Default", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "Extension", null, "xml").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "ContentType", null, "application/octet-stream").ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false); // Default
            await writer.WriteEndElementAsync().ConfigureAwait(false); // Types
            await writer.WriteEndDocumentAsync().ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        private async Task WriteDataXmlAsync(MigrationData data, Stream stream, IProgressReporter? progress, CancellationToken cancellationToken)
        {
            var settings = new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                Encoding = new UTF8Encoding(false)
            };

#if NET8_0_OR_GREATER
            await using var writer = XmlWriter.Create(stream, settings);
#else
            using var writer = XmlWriter.Create(stream, settings);
#endif

            await writer.WriteStartDocumentAsync().ConfigureAwait(false);
            await writer.WriteStartElementAsync(null, "entities", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "timestamp", null, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")).ConfigureAwait(false);

            foreach (var (entityName, records) in data.EntityData)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Get display name from schema
                var entitySchema = data.Schema.Entities.FirstOrDefault(e => e.LogicalName == entityName);
                var displayName = entitySchema?.DisplayName ?? entityName;

                await writer.WriteStartElementAsync(null, "entity", null).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "name", null, entityName).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "displayname", null, displayName).ConfigureAwait(false);

                await writer.WriteStartElementAsync(null, "records", null).ConfigureAwait(false);

                foreach (var record in records)
                {
                    await WriteRecordAsync(writer, record).ConfigureAwait(false);
                }

                await writer.WriteEndElementAsync().ConfigureAwait(false); // records

                // Write m2mrelationships
                await writer.WriteStartElementAsync(null, "m2mrelationships", null).ConfigureAwait(false);
                if (data.RelationshipData.TryGetValue(entityName, out var m2mList))
                {
                    foreach (var m2m in m2mList)
                    {
                        await WriteM2MRelationshipAsync(writer, m2m).ConfigureAwait(false);
                    }
                }
                await writer.WriteEndElementAsync().ConfigureAwait(false); // m2mrelationships

                await writer.WriteEndElementAsync().ConfigureAwait(false); // entity
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // entities
            await writer.WriteEndDocumentAsync().ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        private async Task WriteRecordAsync(XmlWriter writer, Entity record)
        {
            await writer.WriteStartElementAsync(null, "record", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "id", null, record.Id.ToString()).ConfigureAwait(false);

            foreach (var attribute in record.Attributes)
            {
                await WriteFieldAsync(writer, attribute.Key, attribute.Value).ConfigureAwait(false);
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // record
        }

        private async Task WriteFieldAsync(XmlWriter writer, string name, object? value)
        {
            if (value == null)
            {
                return;
            }

            await writer.WriteStartElementAsync(null, "field", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "name", null, name).ConfigureAwait(false);

            // CMT format: value in attribute, type-specific additional attributes
            string stringValue;
            switch (value)
            {
                case EntityReference er:
                    await writer.WriteAttributeStringAsync(null, "value", null, er.Id.ToString()).ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "lookupentity", null, er.LogicalName).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(er.Name))
                    {
                        await writer.WriteAttributeStringAsync(null, "lookupentityname", null, er.Name).ConfigureAwait(false);
                    }
                    break;

                case OptionSetValue osv:
                    await writer.WriteAttributeStringAsync(null, "value", null, osv.Value.ToString()).ConfigureAwait(false);
                    break;

                case Money m:
                    await writer.WriteAttributeStringAsync(null, "value", null, m.Value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;

                case DateTime dt:
                    // CMT uses ISO 8601 format with 7 decimal places
                    stringValue = dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
                    await writer.WriteAttributeStringAsync(null, "value", null, stringValue).ConfigureAwait(false);
                    break;

                case bool b:
                    await writer.WriteAttributeStringAsync(null, "value", null, b ? "1" : "0").ConfigureAwait(false);
                    break;

                case Guid g:
                    await writer.WriteAttributeStringAsync(null, "value", null, g.ToString()).ConfigureAwait(false);
                    break;

                case int i:
                    await writer.WriteAttributeStringAsync(null, "value", null, i.ToString()).ConfigureAwait(false);
                    break;

                case decimal d:
                    await writer.WriteAttributeStringAsync(null, "value", null, d.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;

                case double dbl:
                    await writer.WriteAttributeStringAsync(null, "value", null, dbl.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;

                default:
                    await writer.WriteAttributeStringAsync(null, "value", null, value.ToString() ?? string.Empty).ConfigureAwait(false);
                    break;
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // field
        }

        private async Task WriteM2MRelationshipAsync(XmlWriter writer, ManyToManyRelationshipData m2m)
        {
            await writer.WriteStartElementAsync(null, "m2mrelationship", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "sourceid", null, m2m.SourceId.ToString()).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "targetentityname", null, m2m.TargetEntityName).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "targetentitynameidfield", null, m2m.TargetEntityPrimaryKey).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "m2mrelationshipname", null, m2m.RelationshipName).ConfigureAwait(false);

            await writer.WriteStartElementAsync(null, "targetids", null).ConfigureAwait(false);
            foreach (var targetId in m2m.TargetIds)
            {
                await writer.WriteElementStringAsync(null, "targetid", null, targetId.ToString()).ConfigureAwait(false);
            }
            await writer.WriteEndElementAsync().ConfigureAwait(false); // targetids

            await writer.WriteEndElementAsync().ConfigureAwait(false); // m2mrelationship
        }

        private async Task WriteSchemaXmlAsync(MigrationSchema schema, Stream stream, CancellationToken cancellationToken)
        {
            var settings = new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                Encoding = new UTF8Encoding(false)
            };

#if NET8_0_OR_GREATER
            await using var writer = XmlWriter.Create(stream, settings);
#else
            using var writer = XmlWriter.Create(stream, settings);
#endif

            await writer.WriteStartDocumentAsync().ConfigureAwait(false);
            await writer.WriteStartElementAsync(null, "entities", null).ConfigureAwait(false);

            foreach (var entity in schema.Entities)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await writer.WriteStartElementAsync(null, "entity", null).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "name", null, entity.LogicalName).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "displayname", null, entity.DisplayName).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "etc", null, (entity.ObjectTypeCode ?? 0).ToString()).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "primaryidfield", null, entity.PrimaryIdField).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "primarynamefield", null, entity.PrimaryNameField).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "disableplugins", null, entity.DisablePlugins.ToString().ToLowerInvariant()).ConfigureAwait(false);

                // Write fields
                await writer.WriteStartElementAsync(null, "fields", null).ConfigureAwait(false);
                foreach (var field in entity.Fields)
                {
                    await writer.WriteStartElementAsync(null, "field", null).ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "displayname", null, field.DisplayName).ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "name", null, field.LogicalName).ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "type", null, field.Type).ConfigureAwait(false);
                    if (field.IsPrimaryKey)
                    {
                        await writer.WriteAttributeStringAsync(null, "primaryKey", null, "true").ConfigureAwait(false);
                    }
                    if (!string.IsNullOrEmpty(field.LookupEntity))
                    {
                        await writer.WriteAttributeStringAsync(null, "lookupType", null, field.LookupEntity).ConfigureAwait(false);
                    }
                    await writer.WriteEndElementAsync().ConfigureAwait(false); // field
                }
                await writer.WriteEndElementAsync().ConfigureAwait(false); // fields

                // Write filter if present (HTML-encoded)
                if (!string.IsNullOrEmpty(entity.FetchXmlFilter))
                {
                    await writer.WriteElementStringAsync(null, "filter", null, entity.FetchXmlFilter).ConfigureAwait(false);
                }

                await writer.WriteEndElementAsync().ConfigureAwait(false); // entity
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // entities
            await writer.WriteEndDocumentAsync().ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }
}
