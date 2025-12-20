using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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
            await writer.WriteAttributeStringAsync(null, "timestamp", null, DateTime.UtcNow.ToString("O")).ConfigureAwait(false);

            foreach (var (entityName, records) in data.EntityData)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await writer.WriteStartElementAsync(null, "entity", null).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "name", null, entityName).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "recordcount", null, records.Count.ToString()).ConfigureAwait(false);

                await writer.WriteStartElementAsync(null, "records", null).ConfigureAwait(false);

                foreach (var record in records)
                {
                    await WriteRecordAsync(writer, record).ConfigureAwait(false);
                }

                await writer.WriteEndElementAsync().ConfigureAwait(false); // records
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
                if (attribute.Key == record.LogicalName + "id")
                {
                    continue; // Skip primary ID field as it's in the record id attribute
                }

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

            switch (value)
            {
                case EntityReference er:
                    await writer.WriteAttributeStringAsync(null, "type", null, "lookup").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, er.Id.ToString()).ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "lookupentity", null, er.LogicalName).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(er.Name))
                    {
                        await writer.WriteAttributeStringAsync(null, "lookupentityname", null, er.Name).ConfigureAwait(false);
                    }
                    break;

                case OptionSetValue osv:
                    await writer.WriteAttributeStringAsync(null, "type", null, "optionset").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, osv.Value.ToString()).ConfigureAwait(false);
                    break;

                case Money m:
                    await writer.WriteAttributeStringAsync(null, "type", null, "money").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, m.Value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;

                case DateTime dt:
                    await writer.WriteAttributeStringAsync(null, "type", null, "datetime").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, dt.ToString("O")).ConfigureAwait(false);
                    break;

                case bool b:
                    await writer.WriteAttributeStringAsync(null, "type", null, "bool").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, b.ToString().ToLowerInvariant()).ConfigureAwait(false);
                    break;

                case Guid g:
                    await writer.WriteAttributeStringAsync(null, "type", null, "guid").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, g.ToString()).ConfigureAwait(false);
                    break;

                case int i:
                    await writer.WriteAttributeStringAsync(null, "type", null, "int").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, i.ToString()).ConfigureAwait(false);
                    break;

                case decimal d:
                    await writer.WriteAttributeStringAsync(null, "type", null, "decimal").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, d.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;

                case double dbl:
                    await writer.WriteAttributeStringAsync(null, "type", null, "float").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, dbl.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;

                default:
                    await writer.WriteAttributeStringAsync(null, "type", null, "string").ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "value", null, value.ToString()).ConfigureAwait(false);
                    break;
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // field
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
            await writer.WriteAttributeStringAsync(null, "version", null, schema.Version).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "timestamp", null, DateTime.UtcNow.ToString("O")).ConfigureAwait(false);

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
                    await writer.WriteAttributeStringAsync(null, "name", null, field.LogicalName).ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "displayname", null, field.DisplayName).ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "type", null, field.Type).ConfigureAwait(false);
                    if (field.IsPrimaryKey)
                    {
                        await writer.WriteAttributeStringAsync(null, "primaryKey", null, "true").ConfigureAwait(false);
                    }
                    if (!string.IsNullOrEmpty(field.LookupEntity))
                    {
                        await writer.WriteAttributeStringAsync(null, "lookupType", null, field.LookupEntity).ConfigureAwait(false);
                    }
                    await writer.WriteAttributeStringAsync(null, "customfield", null, field.IsCustomField.ToString().ToLowerInvariant()).ConfigureAwait(false);
                    await writer.WriteEndElementAsync().ConfigureAwait(false); // field
                }
                await writer.WriteEndElementAsync().ConfigureAwait(false); // fields

                // Write relationships
                if (entity.Relationships.Count > 0)
                {
                    await writer.WriteStartElementAsync(null, "relationships", null).ConfigureAwait(false);
                    foreach (var rel in entity.Relationships)
                    {
                        await writer.WriteStartElementAsync(null, "relationship", null).ConfigureAwait(false);
                        await writer.WriteAttributeStringAsync(null, "name", null, rel.Name).ConfigureAwait(false);
                        await writer.WriteAttributeStringAsync(null, "manyToMany", null, rel.IsManyToMany.ToString().ToLowerInvariant()).ConfigureAwait(false);
                        await writer.WriteAttributeStringAsync(null, "relatedEntityName", null, rel.Entity2).ConfigureAwait(false);

                        if (rel.IsManyToMany)
                        {
                            // M2M relationship attributes
                            await writer.WriteAttributeStringAsync(null, "m2mTargetEntity", null, rel.Entity2).ConfigureAwait(false);
                            await writer.WriteAttributeStringAsync(null, "m2mTargetEntityPrimaryKey", null, rel.Entity2Attribute).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(rel.IntersectEntity))
                            {
                                await writer.WriteAttributeStringAsync(null, "intersectEntityName", null, rel.IntersectEntity).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            // One-to-many relationship attributes
                            await writer.WriteAttributeStringAsync(null, "referencingEntity", null, rel.Entity1).ConfigureAwait(false);
                            await writer.WriteAttributeStringAsync(null, "referencingAttribute", null, rel.Entity1Attribute).ConfigureAwait(false);
                            await writer.WriteAttributeStringAsync(null, "referencedEntity", null, rel.Entity2).ConfigureAwait(false);
                            await writer.WriteAttributeStringAsync(null, "referencedAttribute", null, rel.Entity2Attribute).ConfigureAwait(false);
                        }

                        await writer.WriteEndElementAsync().ConfigureAwait(false); // relationship
                    }
                    await writer.WriteEndElementAsync().ConfigureAwait(false); // relationships
                }

                await writer.WriteEndElementAsync().ConfigureAwait(false); // entity
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // entities
            await writer.WriteEndDocumentAsync().ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }
}
