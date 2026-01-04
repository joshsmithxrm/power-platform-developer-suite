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

        var attributesByName = ColumnMatcher.BuildAttributeLookup(
            entityMetadata, includeDisplayNames: true, filterValidForUpdate: true);

        var config = new CsvMappingConfig
        {
            Schema = CsvMappingSchema.SchemaUrl,
            Version = CsvMappingSchema.CurrentVersion,
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
        var attributesByName = ColumnMatcher.BuildAttributeLookup(
            entityMetadata, includeDisplayNames: true, filterValidForUpdate: true);
        var result = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            var normalizedHeader = ColumnMatcher.NormalizeForMatching(header);

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
        while (await csv.ReadAsync() && rowCount < ColumnMatcher.MaxSampleValues)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var header in headers)
            {
                var value = csv.GetField(header);
                if (!string.IsNullOrEmpty(value)
                    && sampleValues[header].Count < ColumnMatcher.MaxSampleValues
                    && !sampleValues[header].Contains(value))
                {
                    sampleValues[header].Add(value);
                }
            }
            rowCount++;
        }

        return (headers, sampleValues);
    }

    private ColumnMappingEntry CreateMappingEntry(
        string header,
        Dictionary<string, AttributeMetadata> attributesByName,
        EntityMetadata entityMetadata,
        List<string>? sampleValues)
    {
        var normalizedHeader = ColumnMatcher.NormalizeForMatching(header);
        var samples = sampleValues?.Take(ColumnMatcher.MaxSampleValues).ToList();

        // Extract publisher prefix from entity name (e.g., "ppds_city" → "ppds_")
        var prefix = ColumnMatcher.ExtractPublisherPrefix(entityMetadata.LogicalName ?? "");

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
            var normalizedPrefixedHeader = ColumnMatcher.NormalizeForMatching(prefix + header);
            foreach (var (attrName, attr) in attributesByName)
            {
                if (ColumnMatcher.NormalizeForMatching(attrName) == normalizedPrefixedHeader)
                {
                    return CreateMatchedEntry(attr, samples, entityMetadata);
                }
            }
        }

        // No match found
        return CreateUnmatchedEntry(header, entityMetadata, samples, prefix);
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
        if (ColumnMatcher.IsLookupAttribute(attr))
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
        if (ColumnMatcher.IsOptionSetAttribute(attr))
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
        var similarAttributes = ColumnMatcher.FindSimilarAttributes(header, entityMetadata, prefix);

        return new ColumnMappingEntry
        {
            Field = null, // Forces explicit decision
            Status = "needs-configuration",
            Note = "No matching attribute found. Set 'field' to map to an attribute, or set 'skip: true' to ignore this column.",
            SimilarAttributes = similarAttributes.Count > 0 ? similarAttributes : null,
            CsvSample = samples?.Count > 0 ? samples : null
        };
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

}
