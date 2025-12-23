using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using PPDS.Migration.Models;

namespace PPDS.Migration.Formats
{
    /// <summary>
    /// Writes CMT-compatible schema files.
    /// </summary>
    public class CmtSchemaWriter : ICmtSchemaWriter
    {
        private readonly ILogger<CmtSchemaWriter>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="CmtSchemaWriter"/> class.
        /// </summary>
        public CmtSchemaWriter()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CmtSchemaWriter"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public CmtSchemaWriter(ILogger<CmtSchemaWriter> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task WriteAsync(MigrationSchema schema, string path, CancellationToken cancellationToken = default)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

            _logger?.LogInformation("Writing schema to {Path}", path);

#if NET8_0_OR_GREATER
            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
#else
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
#endif
            await WriteAsync(schema, stream, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task WriteAsync(MigrationSchema schema, Stream stream, CancellationToken cancellationToken = default)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (stream == null) throw new ArgumentNullException(nameof(stream));

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
                await WriteEntityAsync(writer, entity).ConfigureAwait(false);
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // entities
            await writer.WriteEndDocumentAsync().ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);

            _logger?.LogInformation("Wrote schema with {Count} entities", schema.Entities.Count);
        }

        private static async Task WriteEntityAsync(XmlWriter writer, EntitySchema entity)
        {
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
                await WriteFieldAsync(writer, field).ConfigureAwait(false);
            }
            await writer.WriteEndElementAsync().ConfigureAwait(false); // fields

            // Write relationships
            if (entity.Relationships.Count > 0)
            {
                await writer.WriteStartElementAsync(null, "relationships", null).ConfigureAwait(false);
                foreach (var rel in entity.Relationships)
                {
                    await WriteRelationshipAsync(writer, rel).ConfigureAwait(false);
                }
                await writer.WriteEndElementAsync().ConfigureAwait(false); // relationships
            }

            // Write filter if present (HTML-encoded)
            if (!string.IsNullOrEmpty(entity.FetchXmlFilter))
            {
                await writer.WriteElementStringAsync(null, "filter", null, entity.FetchXmlFilter).ConfigureAwait(false);
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // entity
        }

        private static async Task WriteFieldAsync(XmlWriter writer, FieldSchema field)
        {
            await writer.WriteStartElementAsync(null, "field", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "displayname", null, field.DisplayName).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "name", null, field.LogicalName).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "type", null, field.Type).ConfigureAwait(false);

            if (field.IsPrimaryKey)
            {
                await writer.WriteAttributeStringAsync(null, "primaryKey", null, "true").ConfigureAwait(false);
            }

            // Write lookupType for lookup fields (may be pipe-delimited for polymorphic)
            if (!string.IsNullOrEmpty(field.LookupEntity))
            {
                await writer.WriteAttributeStringAsync(null, "lookupType", null, field.LookupEntity).ConfigureAwait(false);
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // field
        }

        private static async Task WriteRelationshipAsync(XmlWriter writer, RelationshipSchema rel)
        {
            await writer.WriteStartElementAsync(null, "relationship", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "name", null, rel.Name).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "manyToMany", null, rel.IsManyToMany.ToString().ToLowerInvariant()).ConfigureAwait(false);

            if (rel.IsManyToMany)
            {
                // M2M relationship attributes - CMT format
                // relatedEntityName = intersect entity (e.g., "systemuserroles")
                // m2mTargetEntity = target entity (e.g., "role")
                // m2mTargetEntityPrimaryKey = target entity's PK (e.g., "roleid")
                await writer.WriteAttributeStringAsync(null, "isreflexive", null, rel.IsReflexive.ToString().ToLowerInvariant()).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "relatedEntityName", null, rel.IntersectEntity ?? rel.Name).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "m2mTargetEntity", null, rel.Entity2).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "m2mTargetEntityPrimaryKey", null, rel.TargetEntityPrimaryKey ?? $"{rel.Entity2}id").ConfigureAwait(false);
            }
            else
            {
                // One-to-many relationship attributes
                await writer.WriteAttributeStringAsync(null, "relatedEntityName", null, rel.Entity2).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "referencingEntity", null, rel.Entity1).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "referencingAttribute", null, rel.Entity1Attribute).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "referencedEntity", null, rel.Entity2).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "referencedAttribute", null, rel.Entity2Attribute).ConfigureAwait(false);
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false); // relationship
        }
    }
}
