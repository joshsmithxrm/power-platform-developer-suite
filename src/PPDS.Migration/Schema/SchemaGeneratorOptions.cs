using System;
using System.Collections.Generic;
using System.Linq;

namespace PPDS.Migration.Schema
{
    /// <summary>
    /// Options for schema generation.
    /// </summary>
    public class SchemaGeneratorOptions
    {
        /// <summary>
        /// Gets or sets whether to include all fields. Default: true.
        /// </summary>
        public bool IncludeAllFields { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to include audit fields (createdon, createdby, modifiedon, modifiedby, etc.). Default: false.
        /// </summary>
        public bool IncludeAuditFields { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to include only custom fields. Default: false.
        /// </summary>
        public bool CustomFieldsOnly { get; set; } = false;

        /// <summary>
        /// Gets or sets the default value for disabling plugins during import. Default: false.
        /// </summary>
        public bool DisablePluginsByDefault { get; set; } = false;

        /// <summary>
        /// Gets or sets the attributes to include (whitelist). If set, only these attributes are included.
        /// Takes precedence over ExcludeAttributes. Primary key is always included.
        /// </summary>
        public IReadOnlyList<string>? IncludeAttributes { get; set; }

        /// <summary>
        /// Gets or sets the attributes to exclude (blacklist). If set, these attributes are excluded.
        /// Ignored if IncludeAttributes is set.
        /// </summary>
        public IReadOnlyList<string>? ExcludeAttributes { get; set; }

        /// <summary>
        /// Gets or sets per-entity FetchXML filter expressions to embed in the generated schema.
        /// Keys are entity logical names; values are FetchXML filter XML strings
        /// (e.g., "&lt;filter&gt;&lt;condition attribute='statecode' operator='eq' value='0'/&gt;&lt;/filter&gt;").
        /// </summary>
        public IReadOnlyDictionary<string, string>? EntityFilters { get; set; }

        /// <summary>
        /// Determines if an attribute should be included based on the filtering options.
        /// </summary>
        /// <param name="attributeName">The attribute logical name.</param>
        /// <param name="isPrimaryKey">Whether this attribute is the primary key.</param>
        /// <returns>True if the attribute should be included.</returns>
        public bool ShouldIncludeAttribute(string attributeName, bool isPrimaryKey)
        {
            // Primary key is always included
            if (isPrimaryKey)
            {
                return true;
            }

            // Whitelist mode: only include specified attributes
            if (IncludeAttributes != null && IncludeAttributes.Count > 0)
            {
                return IncludeAttributes.Any(attr => attr.Equals(attributeName, StringComparison.OrdinalIgnoreCase));
            }

            // Blacklist mode: exclude specified attributes
            if (ExcludeAttributes?.Any(attr => attr.Equals(attributeName, StringComparison.OrdinalIgnoreCase)) == true)
            {
                return false;
            }

            return true;
        }
    }
}
