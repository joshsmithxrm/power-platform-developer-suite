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
            var importIssues = new List<string>();

            foreach (var entityElement in entitiesElement.Elements("entity"))
            {
                var entity = ParseEntity(entityElement, importIssues);
                entities.Add(entity);
            }

            return new MigrationSchema
            {
                Version = root.Attribute("version")?.Value ?? "1.0",
                GeneratedAt = ParseDateTime(root.Attribute("timestamp")?.Value),
                Entities = entities,
                ImportIssues = importIssues
            };
        }

        /// <summary>
        /// Reads an import-only attribute. A value the import cannot apply is not an error of the
        /// schema as such (an export does not read these attributes): it is recorded in
        /// <paramref name="importIssues"/>, and an import refuses a schema that has any
        /// (#1934 r2 should-fix 4).
        /// </summary>
        private static T ReadForImport<T>(Func<T> read, T fallback, List<string> importIssues)
        {
            try
            {
                return read();
            }
            catch (InvalidDataException ex)
            {
                importIssues.Add(ex.Message);
                return fallback;
            }
        }

        private EntitySchema ParseEntity(XElement element, List<string> importIssues)
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
            var disablePlugins = ReadForImport(() => ReadDisablePlugins(element, logicalName), false, importIssues);
            var importMode = ReadForImport(() => ReadImportMode(element, logicalName), null, importIssues);
            ReadForImport(() => { RefuseUnappliedCmtAttributes(element, logicalName); return 0; }, 0, importIssues);

            var fields = new List<FieldSchema>();
            var fieldsElement = element.Element("fields");
            if (fieldsElement != null)
            {
                foreach (var fieldElement in fieldsElement.Elements("field"))
                {
                    var field = ParseField(fieldElement, importIssues);
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

        private FieldSchema ParseField(XElement element, List<string> importIssues)
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
            var dateMode = ReadForImport(() => ReadDateMode(element, logicalName), DateMode.Absolute, importIssues);

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

        /// <summary>
        /// Finds an attribute by name in any case. Two attributes that differ only in case are refused:
        /// which one applies would be a guess.
        /// </summary>
        private static XAttribute? FindAttribute(XElement element, string name, string owner)
        {
            var attributes = element.Attributes()
                .Where(a => a.Name.NamespaceName.Length == 0
                    && string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (attributes.Count > 1)
            {
                throw new InvalidDataException($"{owner} in the schema has more than one {name} attribute. Keep one.");
            }

            return attributes.Count == 1 ? attributes[0] : null;
        }

        /// <summary>
        /// Reads <c>disableplugins</c>: true, false, 1, 0, yes or no, in any case with surrounding
        /// spaces ignored; absent is false. Anything else is refused: it used to read as false, so
        /// the table's plug-in steps silently stayed on (#1921 review should-fix 4).
        /// </summary>
        private static bool ReadDisablePlugins(XElement element, string logicalName)
        {
            var attribute = FindAttribute(element, "disableplugins", $"Table '{logicalName}'");
            if (attribute == null)
            {
                return false;
            }

            return attribute.Value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => throw new InvalidDataException(
                    $"Table '{logicalName}' in the schema has a disableplugins value that is not one of true, false, 1, 0, yes or no. Correct it.")
            };
        }

        /// <summary>
        /// Refuses the CMT entity attributes <c>skipupdate</c> and <c>forcecreate</c> when they ask for
        /// something: PPDS does not apply them, and ignoring them imported the table with the import's
        /// mode (Upsert by default), overwriting what <c>skipupdate</c> meant to keep (#1914 review
        /// should-fix 3). A value of false asks for nothing and is accepted.
        /// </summary>
        private static void RefuseUnappliedCmtAttributes(XElement element, string logicalName)
        {
            foreach (var name in new[] { "skipupdate", "forcecreate" })
            {
                var attribute = FindAttribute(element, name, $"Table '{logicalName}'");
                if (attribute == null)
                {
                    continue;
                }

                var value = attribute.Value.Trim().ToLowerInvariant();
                if (value is "false" or "0" or "no")
                {
                    continue;
                }

                throw new InvalidDataException(
                    $"Table '{logicalName}' in the schema carries the Configuration Migration Tool attribute {name}, which PPDS does not apply. " +
                    "Remove it, and set importMode on the table instead (create, update, upsert or skip).");
            }
        }

        /// <summary>
        /// Reads a field's <c>dateMode</c>: absolute, relative, relativeDaily or relativeExact, in any
        /// case with surrounding spaces ignored; absent is absolute. Anything else is refused: it used
        /// to read as absolute (#1914 review should-fix 5).
        /// </summary>
        private static DateMode ReadDateMode(XElement element, string fieldName)
        {
            var attribute = FindAttribute(element, "dateMode", $"Field '{fieldName}'");
            if (attribute == null)
            {
                return DateMode.Absolute;
            }

            return attribute.Value.Trim().ToLowerInvariant() switch
            {
                "absolute" => DateMode.Absolute,
                "relative" => DateMode.Relative,
                "relativedaily" => DateMode.RelativeDaily,
                "relativeexact" => DateMode.RelativeExact,
                _ => throw new InvalidDataException(
                    $"Field '{fieldName}' in the schema has a dateMode that is not one of absolute, relative, relativeDaily or relativeExact. Correct it, or remove it.")
            };
        }

        /// <summary>
        /// Reads the per-table <c>importMode</c> attribute (attribute name in any case, value in any
        /// case with surrounding spaces ignored). A value that is not a mode is refused: it used to be
        /// ignored, so the table took the global mode (Upsert by default) and a mistyped "skip"
        /// overwrote the table's existing records (#1914).
        /// </summary>
        private static ImportMode? ReadImportMode(XElement element, string logicalName)
        {
            var attribute = FindAttribute(element, "importMode", $"Table '{logicalName}'");
            if (attribute == null)
            {
                return null;
            }

            return attribute.Value.Trim().ToLowerInvariant() switch
            {
                "create" => ImportMode.Create,
                "update" => ImportMode.Update,
                "upsert" => ImportMode.Upsert,
                "skip" => ImportMode.Skip,
                _ => throw new InvalidDataException(
                    $"Table '{logicalName}' in the schema has an importMode that is not one of create, update, upsert or skip. " +
                    "Correct it, or remove it to use the import's mode.")
            };
        }
    }
}
