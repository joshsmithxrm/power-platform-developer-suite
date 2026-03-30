using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PPDS.Migration.Import;
using PPDS.Migration.Models;

namespace PPDS.Migration.Formats
{
    /// <summary>
    /// Reads CMT schema.xml files.
    /// </summary>
    public class CmtSchemaReader : ICmtSchemaReader
    {
        private readonly ILogger<CmtSchemaReader>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="CmtSchemaReader"/> class.
        /// </summary>
        public CmtSchemaReader()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CmtSchemaReader"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public CmtSchemaReader(ILogger<CmtSchemaReader> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<MigrationSchema> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Schema file not found: {path}", path);
            }

            _logger?.LogInformation("Reading schema from {Path}", path);

#if NET8_0_OR_GREATER
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
#else
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
#endif
            return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<MigrationSchema> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

#if NET8_0_OR_GREATER
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
#else
            var doc = XDocument.Load(stream, LoadOptions.None);
            await Task.CompletedTask; // Keep async signature
#endif

            var schema = ParseSchema(doc);

            _logger?.LogInformation("Parsed schema with {EntityCount} entities", schema.Entities.Count);

            return schema;
        }

        private MigrationSchema ParseSchema(XDocument doc)
        {
            var root = doc.Root ?? throw new InvalidOperationException("Schema XML has no root element");

            // CMT format: <entities> root or <ImportExportXml> with <entities> child
            var entitiesElement = root.Name.LocalName.Equals("entities", StringComparison.OrdinalIgnoreCase)
                ? root
                : root.Element("entities") ?? throw new InvalidOperationException("Schema XML has no <entities> element");

            var entities = new List<EntitySchema>();

            foreach (var entityElement in entitiesElement.Elements("entity"))
            {
                var entity = ParseEntity(entityElement);
                entities.Add(entity);
            }

            return new MigrationSchema
            {
                Version = root.Attribute("version")?.Value ?? "1.0",
                GeneratedAt = ParseDateTime(root.Attribute("timestamp")?.Value),
                Entities = entities
            };
        }

        private EntitySchema ParseEntity(XElement element)
        {
            var logicalName = element.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(logicalName))
            {
                throw new InvalidOperationException("Entity element is missing required 'name' attribute.");
            }
            var displayName = element.Attribute("displayname")?.Value ?? logicalName;
            var objectTypeCode = ParseInt(element.Attribute("etc")?.Value);
            var primaryIdField = element.Attribute("primaryidfield")?.Value ?? $"{logicalName}id";
            var primaryNameField = element.Attribute("primarynamefield")?.Value ?? "name";
            var disablePlugins = ParseBool(element.Attribute("disableplugins")?.Value);
            var importModeAttr = element.Attribute("importMode")?.Value;
            var importMode = ParseImportMode(importModeAttr);

            var fields = new List<FieldSchema>();
            var fieldsElement = element.Element("fields");
            if (fieldsElement != null)
            {
                foreach (var fieldElement in fieldsElement.Elements("field"))
                {
                    var field = ParseField(fieldElement);
                    fields.Add(field);
                }
            }

            var relationships = new List<RelationshipSchema>();
            var relationshipsElement = element.Element("relationships");
            if (relationshipsElement != null)
            {
                foreach (var relElement in relationshipsElement.Elements("relationship"))
                {
                    var relationship = ParseRelationship(relElement, logicalName);
                    relationships.Add(relationship);
                }
            }

            // Check for filter element — use inner XML to preserve nested <filter>/<condition> elements
            // (.Value only returns concatenated text content, losing element structure)
            var filterElement = element.Element("filter");
            string? fetchXmlFilter = null;
            if (filterElement != null)
            {
                fetchXmlFilter = filterElement.HasElements
                    ? string.Concat(filterElement.Nodes())
                    : filterElement.Value;
            }

            return new EntitySchema
            {
                LogicalName = logicalName,
                DisplayName = displayName,
                PrimaryIdField = primaryIdField,
                PrimaryNameField = primaryNameField,
                DisablePlugins = disablePlugins,
                ObjectTypeCode = objectTypeCode,
                ImportMode = importMode,
                Fields = fields,
                Relationships = relationships,
                FetchXmlFilter = fetchXmlFilter
            };
        }

        private FieldSchema ParseField(XElement element)
        {
            var logicalName = element.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(logicalName))
            {
                throw new InvalidOperationException("Field element is missing required 'name' attribute.");
            }
            var displayName = element.Attribute("displayname")?.Value ?? logicalName;
            var type = element.Attribute("type")?.Value ?? "string";
            var lookupEntity = element.Attribute("lookupType")?.Value;
            var isCustomField = ParseBool(element.Attribute("customfield")?.Value);
            var isRequired = ParseBool(element.Attribute("isrequired")?.Value);
            var isPrimaryKey = ParseBool(element.Attribute("primaryKey")?.Value);

            // Parse validity flags - default to true for backwards compatibility
            var isValidForCreate = ParseBool(element.Attribute("isValidForCreate")?.Value, defaultValue: true);
            var isValidForUpdate = ParseBool(element.Attribute("isValidForUpdate")?.Value, defaultValue: true);
            var dateModeAttr = element.Attribute("dateMode")?.Value;
            var dateMode = ParseDateMode(dateModeAttr);

            return new FieldSchema
            {
                LogicalName = logicalName,
                DisplayName = displayName,
                Type = type,
                LookupEntity = lookupEntity,
                IsCustomField = isCustomField,
                IsRequired = isRequired,
                IsPrimaryKey = isPrimaryKey,
                IsValidForCreate = isValidForCreate,
                IsValidForUpdate = isValidForUpdate,
                DateMode = dateMode,
                MaxLength = ParseInt(element.Attribute("maxlength")?.Value),
                Precision = ParseInt(element.Attribute("precision")?.Value),
                MaxFileSizeKB = ParseInt(element.Attribute("maxFileSizeKB")?.Value)
            };
        }

        private RelationshipSchema ParseRelationship(XElement element, string parentEntity)
        {
            var name = element.Attribute("name")?.Value ?? string.Empty;
            var isManyToMany = ParseBool(element.Attribute("manyToMany")?.Value);
            var isReflexive = ParseBool(element.Attribute("isreflexive")?.Value);
            var relatedEntity = element.Attribute("relatedEntityName")?.Value ?? string.Empty;
            var intersectEntity = element.Attribute("intersectEntityName")?.Value;

            string entity1, entity1Attribute, entity2, entity2Attribute;
            string? targetEntityPrimaryKey = null;

            if (isManyToMany)
            {
                // M2M relationship
                // In CMT format: relatedEntityName is the intersect entity, m2mTargetEntity is the target
                entity1 = parentEntity;
                entity1Attribute = string.Empty;
                entity2 = element.Attribute("m2mTargetEntity")?.Value ?? relatedEntity;
                entity2Attribute = string.Empty;
                targetEntityPrimaryKey = element.Attribute("m2mTargetEntityPrimaryKey")?.Value;
                // If intersectEntity not explicitly set, use relatedEntityName for M2M
                if (string.IsNullOrEmpty(intersectEntity))
                {
                    intersectEntity = relatedEntity;
                }
            }
            else
            {
                // One-to-Many relationship
                entity1 = element.Attribute("referencingEntity")?.Value ?? parentEntity;
                entity1Attribute = element.Attribute("referencingAttribute")?.Value ?? string.Empty;
                entity2 = element.Attribute("referencedEntity")?.Value ?? relatedEntity;
                entity2Attribute = element.Attribute("referencedAttribute")?.Value ?? string.Empty;
            }

            return new RelationshipSchema
            {
                Name = name,
                Entity1 = entity1,
                Entity1Attribute = entity1Attribute,
                Entity2 = entity2,
                Entity2Attribute = entity2Attribute,
                IsManyToMany = isManyToMany,
                IsReflexive = isReflexive,
                IntersectEntity = intersectEntity,
                TargetEntityPrimaryKey = targetEntityPrimaryKey
            };
        }

        private static bool ParseBool(string? value, bool defaultValue = false)
        {
            if (string.IsNullOrEmpty(value))
            {
                return defaultValue;
            }

            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("1", StringComparison.Ordinal) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static int? ParseInt(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return int.TryParse(value, out var result) ? result : null;
        }

        private static DateTime? ParseDateTime(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return DateTime.TryParse(value, out var result) ? result : null;
        }

        private static DateMode ParseDateMode(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return DateMode.Absolute;
            }

            return value.ToLowerInvariant() switch
            {
                "relative" => DateMode.Relative,
                "relativedaily" => DateMode.RelativeDaily,
                "relativeexact" => DateMode.RelativeExact,
                _ => DateMode.Absolute
            };
        }

        private static ImportMode? ParseImportMode(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return value.ToLowerInvariant() switch
            {
                "create" => ImportMode.Create,
                "update" => ImportMode.Update,
                "upsert" => ImportMode.Upsert,
                "skip" => ImportMode.Skip,
                _ => null
            };
        }
    }
}
