using Microsoft.Xrm.Sdk.Metadata;

namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Provides column-to-attribute matching utilities for CSV import operations.
/// Used by both CsvDataLoader (loading) and MappingGenerator (mapping file generation).
/// </summary>
public static class ColumnMatcher
{
    /// <summary>
    /// Maximum number of sample values to collect from CSV for preview.
    /// </summary>
    public const int MaxSampleValues = 3;

    /// <summary>
    /// Extracts the publisher prefix from an entity logical name.
    /// </summary>
    /// <param name="entityLogicalName">Entity logical name (e.g., "ppds_city").</param>
    /// <returns>Publisher prefix including underscore (e.g., "ppds_"), or null if no prefix.</returns>
    public static string? ExtractPublisherPrefix(string entityLogicalName)
    {
        // Standard Dataverse entity naming: <publisher>_<name>
        // Extract the prefix including underscore: "ppds_city" → "ppds_"
        var underscoreIndex = entityLogicalName.IndexOf('_');
        if (underscoreIndex > 0)
        {
            return entityLogicalName[..(underscoreIndex + 1)];
        }
        return null;
    }

    /// <summary>
    /// Normalizes a value for fuzzy matching by removing spaces, underscores, hyphens, and lowercasing.
    /// </summary>
    /// <param name="value">Value to normalize.</param>
    /// <returns>Normalized value for comparison.</returns>
    public static string NormalizeForMatching(string value)
    {
        return value
            .Replace(" ", "")
            .Replace("_", "")
            .Replace("-", "")
            .ToLowerInvariant();
    }

    /// <summary>
    /// Determines if an attribute is a lookup type (Lookup, Customer, or Owner).
    /// </summary>
    /// <param name="attr">Attribute metadata.</param>
    /// <returns>True if the attribute is a lookup type.</returns>
    public static bool IsLookupAttribute(AttributeMetadata attr)
    {
        return attr.AttributeType == AttributeTypeCode.Lookup ||
               attr.AttributeType == AttributeTypeCode.Customer ||
               attr.AttributeType == AttributeTypeCode.Owner;
    }

    /// <summary>
    /// Determines if an attribute is an option set type (Picklist, State, or Status).
    /// </summary>
    /// <param name="attr">Attribute metadata.</param>
    /// <returns>True if the attribute is an option set type.</returns>
    public static bool IsOptionSetAttribute(AttributeMetadata attr)
    {
        return attr.AttributeType == AttributeTypeCode.Picklist ||
               attr.AttributeType == AttributeTypeCode.State ||
               attr.AttributeType == AttributeTypeCode.Status;
    }

    /// <summary>
    /// Finds similar attributes to a header for suggestions.
    /// </summary>
    /// <param name="header">CSV column header.</param>
    /// <param name="attributes">Dictionary of attribute name to metadata.</param>
    /// <param name="prefix">Optional publisher prefix (e.g., "ppds_").</param>
    /// <returns>List of similar attribute names, ordered by relevance.</returns>
    public static List<string> FindSimilarAttributes(
        string header,
        Dictionary<string, AttributeMetadata> attributes,
        string? prefix = null)
    {
        var normalizedHeader = NormalizeForMatching(header);
        var normalizedPrefixedHeader = prefix != null ? NormalizeForMatching(prefix + header) : null;
        var results = new List<(string Name, int Score, bool PrefixMatch)>();

        foreach (var (attrName, _) in attributes)
        {
            var normalizedAttr = NormalizeForMatching(attrName);

            // Check for prefixed match first (higher priority)
            if (normalizedPrefixedHeader != null && normalizedAttr.Contains(normalizedPrefixedHeader))
            {
                var score = Math.Abs(normalizedAttr.Length - normalizedPrefixedHeader.Length);
                results.Add((attrName, score, true));
            }
            // Check if one contains the other
            else if (normalizedAttr.Contains(normalizedHeader) || normalizedHeader.Contains(normalizedAttr))
            {
                var score = Math.Abs(normalizedAttr.Length - normalizedHeader.Length);
                results.Add((attrName, score, false));
            }
        }

        return results
            .OrderByDescending(r => r.PrefixMatch) // Prefix matches first
            .ThenBy(r => r.Score)
            .Take(MaxSampleValues)
            .Select(r => r.Name)
            .ToList();
    }

    /// <summary>
    /// Finds similar attributes to a header for suggestions using EntityMetadata directly.
    /// </summary>
    /// <param name="header">CSV column header.</param>
    /// <param name="entityMetadata">Entity metadata containing attributes.</param>
    /// <param name="prefix">Optional publisher prefix (e.g., "ppds_").</param>
    /// <returns>List of similar attribute names, ordered by relevance.</returns>
    public static List<string> FindSimilarAttributes(
        string header,
        EntityMetadata entityMetadata,
        string? prefix = null)
    {
        if (entityMetadata.Attributes == null)
        {
            return [];
        }

        var normalizedHeader = NormalizeForMatching(header);
        var normalizedPrefixedHeader = prefix != null ? NormalizeForMatching(prefix + header) : null;
        var results = new List<(string Name, int Score, bool PrefixMatch)>();

        foreach (var attr in entityMetadata.Attributes)
        {
            if (attr.LogicalName == null || !attr.IsValidForUpdate.GetValueOrDefault())
            {
                continue;
            }

            var normalizedAttr = NormalizeForMatching(attr.LogicalName);

            // Check for prefixed match first (higher priority)
            if (normalizedPrefixedHeader != null && normalizedAttr.Contains(normalizedPrefixedHeader))
            {
                var score = Math.Abs(normalizedAttr.Length - normalizedPrefixedHeader.Length);
                results.Add((attr.LogicalName, score, true));
            }
            // Check if one contains the other
            else if (normalizedAttr.Contains(normalizedHeader) || normalizedHeader.Contains(normalizedAttr))
            {
                var score = Math.Abs(normalizedAttr.Length - normalizedHeader.Length);
                results.Add((attr.LogicalName, score, false));
            }
        }

        return results
            .OrderByDescending(r => r.PrefixMatch) // Prefix matches first
            .ThenBy(r => r.Score)
            .Take(MaxSampleValues)
            .Select(r => r.Name)
            .ToList();
    }

    /// <summary>
    /// Builds a lookup dictionary from entity metadata for efficient attribute matching.
    /// </summary>
    /// <param name="entityMetadata">Entity metadata.</param>
    /// <param name="includeDisplayNames">If true, also index by display name.</param>
    /// <param name="filterValidForUpdate">If true, only include attributes valid for update.</param>
    /// <returns>Dictionary mapping attribute names to metadata.</returns>
    public static Dictionary<string, AttributeMetadata> BuildAttributeLookup(
        EntityMetadata entityMetadata,
        bool includeDisplayNames = false,
        bool filterValidForUpdate = false)
    {
        var lookup = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);

        if (entityMetadata.Attributes == null)
        {
            return lookup;
        }

        foreach (var attr in entityMetadata.Attributes)
        {
            if (attr.LogicalName == null)
            {
                continue;
            }

            if (filterValidForUpdate && !attr.IsValidForUpdate.GetValueOrDefault())
            {
                continue;
            }

            // Index by logical name
            lookup[attr.LogicalName] = attr;

            // Also index by display name if requested
            if (includeDisplayNames)
            {
                var displayName = attr.DisplayName?.UserLocalizedLabel?.Label;
                if (!string.IsNullOrEmpty(displayName))
                {
                    var normalizedDisplay = NormalizeForMatching(displayName);
                    if (!lookup.ContainsKey(normalizedDisplay))
                    {
                        lookup[normalizedDisplay] = attr;
                    }
                }
            }
        }

        return lookup;
    }

    /// <summary>
    /// Attempts to find an attribute by normalized header match.
    /// </summary>
    /// <param name="normalizedHeader">Normalized header to match.</param>
    /// <param name="attributes">Attribute dictionary to search.</param>
    /// <param name="found">Found attribute if match succeeds.</param>
    /// <returns>True if a match was found.</returns>
    public static bool TryFindAttribute(
        string normalizedHeader,
        Dictionary<string, AttributeMetadata> attributes,
        out AttributeMetadata? found)
    {
        foreach (var kvp in attributes)
        {
            if (NormalizeForMatching(kvp.Key) == normalizedHeader)
            {
                found = kvp.Value;
                return true;
            }
        }

        found = null;
        return false;
    }
}
