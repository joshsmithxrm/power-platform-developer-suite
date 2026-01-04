using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Xrm.Sdk.Metadata;

namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Generates CSV mapping configuration from CSV headers and entity metadata.
/// </summary>
public sealed class MappingGenerator
{
    private const int MaxSampleValues = 3;

    /// <summary>
    /// Generates a mapping configuration from CSV headers and entity metadata.
    /// </summary>
    /// <param name="csvPath">Path to the CSV file.</param>
    /// <param name="entityMetadata">Entity metadata from Dataverse.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Generated mapping configuration.</returns>
    public async Task<CsvMappingConfig> GenerateAsync(
        string csvPath,
        EntityMetadata entityMetadata,
        CancellationToken cancellationToken = default)
    {
        var (headers, sampleValues) = await ReadCsvHeadersAndSamplesAsync(csvPath, cancellationToken);

        var attributesByName = BuildAttributeLookup(entityMetadata);

        var config = new CsvMappingConfig
        {
            Schema = CsvMappingConfig.SchemaUrl,
            Version = "1.0",
            Entity = entityMetadata.LogicalName,
            GeneratedAt = DateTimeOffset.UtcNow,
            Columns = new Dictionary<string, ColumnMappingEntry>()
        };

        foreach (var header in headers)
        {
            var entry = CreateMappingEntry(
                header,
                attributesByName,
                entityMetadata,
                sampleValues.GetValueOrDefault(header));

            config.Columns[header] = entry;
        }

        return config;
    }

    /// <summary>
    /// Auto-maps CSV headers to entity attributes without generating metadata.
    /// </summary>
    public Dictionary<string, AttributeMetadata> AutoMapHeaders(
        IEnumerable<string> headers,
        EntityMetadata entityMetadata)
    {
        var attributesByName = BuildAttributeLookup(entityMetadata);
        var result = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            var normalizedHeader = NormalizeForMatching(header);

            if (attributesByName.TryGetValue(normalizedHeader, out var attr))
            {
                result[header] = attr;
            }
        }

        return result;
    }

    private async Task<(string[] Headers, Dictionary<string, List<string>> SampleValues)> ReadCsvHeadersAndSamplesAsync(
        string csvPath,
        CancellationToken cancellationToken)
    {
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null
        };

        using var reader = new StreamReader(csvPath);
        using var csv = new CsvReader(reader, csvConfig);

        await csv.ReadAsync();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];

        // Collect sample values for each column
        var sampleValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            sampleValues[header] = new List<string>();
        }

        var rowCount = 0;
        while (await csv.ReadAsync() && rowCount < MaxSampleValues)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var header in headers)
            {
                var value = csv.GetField(header);
                if (!string.IsNullOrEmpty(value)
                    && sampleValues[header].Count < MaxSampleValues
                    && !sampleValues[header].Contains(value))
                {
                    sampleValues[header].Add(value);
                }
            }
            rowCount++;
        }

        return (headers, sampleValues);
    }

    private static Dictionary<string, AttributeMetadata> BuildAttributeLookup(EntityMetadata entityMetadata)
    {
        var lookup = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);

        if (entityMetadata.Attributes == null)
        {
            return lookup;
        }

        foreach (var attr in entityMetadata.Attributes)
        {
            if (attr.LogicalName == null || !attr.IsValidForUpdate.GetValueOrDefault())
            {
                continue;
            }

            // Index by logical name
            lookup[attr.LogicalName] = attr;

            // Also index by display name if different
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

        return lookup;
    }

    private static string NormalizeForMatching(string value)
    {
        // Remove spaces, underscores, and convert to lowercase for matching
        return value
            .Replace(" ", "")
            .Replace("_", "")
            .Replace("-", "")
            .ToLowerInvariant();
    }

    private ColumnMappingEntry CreateMappingEntry(
        string header,
        Dictionary<string, AttributeMetadata> attributesByName,
        EntityMetadata entityMetadata,
        List<string>? sampleValues)
    {
        var normalizedHeader = NormalizeForMatching(header);
        var samples = sampleValues?.Take(MaxSampleValues).ToList();

        // Extract publisher prefix from entity name (e.g., "ppds_city" → "ppds_")
        var prefix = ExtractPublisherPrefix(entityMetadata.LogicalName ?? "");

        // Try exact match first
        if (attributesByName.TryGetValue(header, out var matchedAttr))
        {
            return CreateMatchedEntry(matchedAttr, samples, entityMetadata);
        }

        // Try prefix + column (e.g., "city" → "ppds_city")
        if (prefix != null && attributesByName.TryGetValue(prefix + header, out matchedAttr))
        {
            return CreateMatchedEntry(matchedAttr, samples, entityMetadata);
        }

        // Try normalized match
        if (attributesByName.TryGetValue(normalizedHeader, out matchedAttr))
        {
            return CreateMatchedEntry(matchedAttr, samples, entityMetadata);
        }

        // Try normalized prefix + column
        if (prefix != null)
        {
            var normalizedPrefixedHeader = NormalizeForMatching(prefix + header);
            foreach (var (attrName, attr) in attributesByName)
            {
                if (NormalizeForMatching(attrName) == normalizedPrefixedHeader)
                {
                    return CreateMatchedEntry(attr, samples, entityMetadata);
                }
            }
        }

        // No match found
        return CreateUnmatchedEntry(header, entityMetadata, samples, prefix);
    }

    private static string? ExtractPublisherPrefix(string entityLogicalName)
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

    private ColumnMappingEntry CreateMatchedEntry(
        AttributeMetadata attr,
        List<string>? samples,
        EntityMetadata entityMetadata)
    {
        var entry = new ColumnMappingEntry
        {
            Field = attr.LogicalName,
            Status = "auto-matched",
            Note = "Remove this entry if auto-match is correct",
            CsvSample = samples?.Count > 0 ? samples : null
        };

        // Add lookup configuration if this is a lookup field
        if (IsLookupAttribute(attr))
        {
            var lookupAttr = (LookupAttributeMetadata)attr;
            var targetEntity = lookupAttr.Targets?.FirstOrDefault() ?? "unknown";

            // Check if samples contain non-GUID values
            var hasNonGuidValues = samples?.Any(s => !CsvRecordParser.IsGuid(s)) ?? false;

            entry.Status = hasNonGuidValues ? "needs-configuration" : "auto-matched";
            entry.Note = hasNonGuidValues
                ? "Lookup field - configure resolution below"
                : "Lookup field with GUID values - no configuration needed";

            entry.Lookup = new LookupConfig
            {
                Entity = targetEntity,
                MatchBy = "guid",
                KeyField = null,
                Options = new List<string>
                {
                    "matchBy: 'guid' - CSV values must be GUIDs",
                    $"matchBy: 'field', keyField: 'name' - match by {targetEntity} name",
                    $"matchBy: 'field', keyField: '<field>' - match by specific field"
                }
            };
        }

        // Add optionset values if this is an optionset field
        if (IsOptionSetAttribute(attr))
        {
            var optionSetValues = GetOptionSetValues(attr);
            if (optionSetValues.Count > 0)
            {
                entry.OptionsetValues = optionSetValues;
                entry.Note = "OptionSet field - add optionsetMap if CSV has labels instead of values";
            }
        }

        return entry;
    }

    private ColumnMappingEntry CreateUnmatchedEntry(
        string header,
        EntityMetadata entityMetadata,
        List<string>? samples,
        string? prefix)
    {
        var similarAttributes = FindSimilarAttributes(header, entityMetadata, prefix);

        return new ColumnMappingEntry
        {
            Field = null, // Forces explicit decision
            Status = "needs-configuration",
            Note = "No matching attribute found. Set 'field' to map to an attribute, or set 'skip: true' to ignore this column.",
            SimilarAttributes = similarAttributes.Count > 0 ? similarAttributes : null,
            CsvSample = samples?.Count > 0 ? samples : null
        };
    }

    private static bool IsLookupAttribute(AttributeMetadata attr)
    {
        return attr.AttributeType == AttributeTypeCode.Lookup ||
               attr.AttributeType == AttributeTypeCode.Customer ||
               attr.AttributeType == AttributeTypeCode.Owner;
    }

    private static bool IsOptionSetAttribute(AttributeMetadata attr)
    {
        return attr.AttributeType == AttributeTypeCode.Picklist ||
               attr.AttributeType == AttributeTypeCode.State ||
               attr.AttributeType == AttributeTypeCode.Status;
    }

    private static Dictionary<string, int> GetOptionSetValues(AttributeMetadata attr)
    {
        var result = new Dictionary<string, int>();

        OptionMetadataCollection? options = attr switch
        {
            PicklistAttributeMetadata picklist => picklist.OptionSet?.Options,
            StateAttributeMetadata state => state.OptionSet?.Options,
            StatusAttributeMetadata status => status.OptionSet?.Options,
            _ => null
        };

        if (options == null)
        {
            return result;
        }

        foreach (var option in options)
        {
            var label = option.Label?.UserLocalizedLabel?.Label;
            if (!string.IsNullOrEmpty(label) && option.Value.HasValue)
            {
                result[label] = option.Value.Value;
            }
        }

        return result;
    }

    private static List<string> FindSimilarAttributes(string header, EntityMetadata entityMetadata, string? prefix)
    {
        if (entityMetadata.Attributes == null)
        {
            return new List<string>();
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
            // Simple similarity: check if one contains the other
            else if (normalizedAttr.Contains(normalizedHeader) || normalizedHeader.Contains(normalizedAttr))
            {
                var score = Math.Abs(normalizedAttr.Length - normalizedHeader.Length);
                results.Add((attr.LogicalName, score, false));
            }
        }

        return results
            .OrderByDescending(r => r.PrefixMatch) // Prefix matches first
            .ThenBy(r => r.Score)
            .Take(3)
            .Select(r => r.Name)
            .ToList();
    }
}
