using System;
using System.Collections.Generic;
using PPDS.Migration.Import;

namespace PPDS.Migration.Models
{
    /// <summary>
    /// Schema definition for an entity.
    /// </summary>
    public class EntitySchema
    {
        /// <summary>
        /// Gets or sets the entity logical name (e.g., "account").
        /// </summary>
        public string LogicalName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the entity display name (e.g., "Account").
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the primary ID field name (e.g., "accountid").
        /// </summary>
        public string PrimaryIdField { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the primary name field (e.g., "name").
        /// </summary>
        public string PrimaryNameField { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether to disable plugins during import.
        /// </summary>
        public bool DisablePlugins { get; set; }

        /// <summary>
        /// Gets or sets the entity type code.
        /// </summary>
        public int? ObjectTypeCode { get; set; }

        /// <summary>
        /// Gets or sets the field definitions.
        /// </summary>
        public IReadOnlyList<FieldSchema> Fields { get; set; } = Array.Empty<FieldSchema>();

        /// <summary>
        /// Gets or sets the relationship definitions.
        /// </summary>
        public IReadOnlyList<RelationshipSchema> Relationships { get; set; } = Array.Empty<RelationshipSchema>();

        /// <summary>
        /// Gets or sets the per-entity import mode override.
        /// When null, the global <see cref="ImportOptions.Mode"/> is used.
        /// </summary>
        public ImportMode? ImportMode { get; set; }

        /// <summary>
        /// Gets or sets the FetchXML filter for export (optional).
        /// </summary>
        public string? FetchXmlFilter { get; set; }

        /// <inheritdoc />
        public override string ToString() => $"{LogicalName} ({DisplayName})";
    }
}
