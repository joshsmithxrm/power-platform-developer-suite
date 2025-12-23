using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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
        /// Gets or sets whether to include system fields (createdon, modifiedon, etc.). Default: false.
        /// </summary>
        public bool IncludeSystemFields { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to include relationships. Default: true.
        /// </summary>
        public bool IncludeRelationships { get; set; } = true;

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
        /// Gets or sets attribute name patterns to exclude (e.g., "new_*", "*_base").
        /// Uses glob-style wildcards (* matches any characters).
        /// </summary>
        public IReadOnlyList<string>? ExcludeAttributePatterns { get; set; }

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

            // Pattern exclusion
            if (ExcludeAttributePatterns?.Any(pattern => MatchesPattern(attributeName, pattern)) == true)
            {
                return false;
            }

            return true;
        }

        private static bool MatchesPattern(string value, string pattern)
        {
            // Convert glob pattern to regex
            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase);
        }
    }
}
