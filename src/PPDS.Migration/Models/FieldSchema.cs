using System;

namespace PPDS.Migration.Models
{
    /// <summary>
    /// Schema definition for a field.
    /// </summary>
    public class FieldSchema
    {
        /// <summary>
        /// Gets or sets the field logical name.
        /// </summary>
        public string LogicalName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the field display name.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the field type (e.g., "string", "lookup", "datetime").
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the target entity for lookup fields.
        /// </summary>
        public string? LookupEntity { get; set; }

        /// <summary>
        /// Gets or sets whether this is a custom field.
        /// </summary>
        public bool IsCustomField { get; set; }

        /// <summary>
        /// Gets or sets whether the field is required.
        /// </summary>
        public bool IsRequired { get; set; }

        /// <summary>
        /// Gets or sets whether this field is the primary key.
        /// </summary>
        public bool IsPrimaryKey { get; set; }

        /// <summary>
        /// Gets or sets whether the field is valid for create operations.
        /// Default is true for backwards compatibility with existing schema files.
        /// </summary>
        public bool IsValidForCreate { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the field is valid for update operations.
        /// Default is true for backwards compatibility with existing schema files.
        /// </summary>
        public bool IsValidForUpdate { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum length for string fields.
        /// </summary>
        public int? MaxLength { get; set; }

        /// <summary>
        /// Gets or sets the precision for decimal/money fields.
        /// </summary>
        public int? Precision { get; set; }

        /// <summary>
        /// Gets whether this field is a lookup type.
        /// </summary>
        public bool IsLookup => Type.Equals("entityreference", StringComparison.OrdinalIgnoreCase) ||
                                Type.Equals("lookup", StringComparison.OrdinalIgnoreCase) ||
                                Type.Equals("customer", StringComparison.OrdinalIgnoreCase) ||
                                Type.Equals("owner", StringComparison.OrdinalIgnoreCase) ||
                                Type.Equals("partylist", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets whether this is a polymorphic lookup (customer, owner).
        /// </summary>
        public bool IsPolymorphicLookup => Type.Equals("customer", StringComparison.OrdinalIgnoreCase) ||
                                           Type.Equals("owner", StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public override string ToString() => $"{LogicalName} ({Type})";
    }
}
