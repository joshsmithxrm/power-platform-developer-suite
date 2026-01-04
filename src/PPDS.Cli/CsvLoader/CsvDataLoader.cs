using System.Diagnostics;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;

namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Loads CSV data into Dataverse entities.
/// </summary>
public sealed class CsvDataLoader
{
    private readonly IDataverseConnectionPool _pool;
    private readonly IBulkOperationExecutor _bulkExecutor;
    private readonly ILogger<CsvDataLoader>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CsvDataLoader"/> class.
    /// </summary>
    public CsvDataLoader(
        IDataverseConnectionPool pool,
        IBulkOperationExecutor bulkExecutor,
        ILogger<CsvDataLoader>? logger = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _bulkExecutor = bulkExecutor ?? throw new ArgumentNullException(nameof(bulkExecutor));
        _logger = logger;
    }

    /// <summary>
    /// Loads CSV data into Dataverse.
    /// </summary>
    public async Task<LoadResult> LoadAsync(
        string csvPath,
        CsvLoadOptions options,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var errors = new List<LoadError>();
        var warnings = new List<string>();

        _logger?.LogInformation("Loading CSV file: {CsvPath}", csvPath);

        // 1. Retrieve entity metadata
        var entityMetadata = await RetrieveEntityMetadataAsync(
            options.EntityLogicalName, cancellationToken);

        // 2. Build attribute lookup
        var attributesByName = BuildAttributeLookup(entityMetadata);

        // 3. Determine column mappings
        Dictionary<string, ColumnMappingEntry> mappings;
        if (options.Mapping?.Columns != null)
        {
            mappings = options.Mapping.Columns;

            // Validate the mapping file against the CSV
            var validationResult = await ValidateMappingFileAsync(csvPath, mappings, cancellationToken);

            // Add stale mapping warnings
            foreach (var stale in validationResult.StaleMappings)
            {
                warnings.Add($"Mapping column '{stale}' not found in CSV (stale mapping entry).");
            }

            // Throw if there are validation errors (unconfigured or missing mappings)
            if (validationResult.UnconfiguredColumns.Count > 0 || validationResult.MissingMappings.Count > 0)
            {
                throw new MappingValidationException(
                    validationResult.UnconfiguredColumns,
                    validationResult.MissingMappings,
                    validationResult.StaleMappings);
            }
        }
        else
        {
            var autoMappingResult = await AutoMapColumnsAsync(csvPath, options.EntityLogicalName, attributesByName, cancellationToken);
            warnings.AddRange(autoMappingResult.Warnings);

            // Check if auto-mapping is incomplete
            if (!autoMappingResult.IsComplete && !options.Force)
            {
                throw new MappingIncompleteException(
                    autoMappingResult.MatchedColumns,
                    autoMappingResult.TotalColumns,
                    autoMappingResult.UnmatchedColumns);
            }

            mappings = autoMappingResult.Mappings;
        }

        // 4. Identify and preload lookup caches
        var lookupResolver = new LookupResolver(_pool);
        var lookupConfigs = GetLookupConfigs(mappings, attributesByName);

        if (lookupConfigs.Any(l => l.Config.MatchBy == "field"))
        {
            _logger?.LogInformation("Preloading lookup caches...");
            await lookupResolver.PreloadLookupsAsync(lookupConfigs, cancellationToken);
        }

        // 5. Parse CSV and build entities
        var (entities, parseErrors) = await BuildEntitiesAsync(
            csvPath,
            options,
            mappings,
            attributesByName,
            lookupResolver,
            cancellationToken);

        errors.AddRange(parseErrors);
        errors.AddRange(lookupResolver.Errors);

        _logger?.LogInformation("Built {Count} entities from CSV", entities.Count);

        // 6. Dry-run mode - just return validation results
        if (options.DryRun)
        {
            stopwatch.Stop();
            return new LoadResult
            {
                TotalRows = entities.Count + errors.Count,
                SuccessCount = entities.Count,
                FailureCount = errors.Count,
                SkippedCount = 0,
                Duration = stopwatch.Elapsed,
                Errors = errors,
                Warnings = warnings
            };
        }

        // 7. Execute bulk upsert
        if (entities.Count == 0)
        {
            stopwatch.Stop();
            return new LoadResult
            {
                TotalRows = errors.Count,
                SuccessCount = 0,
                FailureCount = errors.Count,
                Duration = stopwatch.Elapsed,
                Errors = errors,
                Warnings = warnings
            };
        }

        _logger?.LogInformation("Executing bulk upsert for {Count} records...", entities.Count);

        var bulkResult = await _bulkExecutor.UpsertMultipleAsync(
            options.EntityLogicalName,
            entities,
            new BulkOperationOptions
            {
                BatchSize = options.BatchSize,
                BypassCustomLogic = options.BypassPlugins,
                BypassPowerAutomateFlows = options.BypassFlows,
                ContinueOnError = options.ContinueOnError
            },
            progress,
            cancellationToken);

        // 8. Map bulk operation errors
        foreach (var bulkError in bulkResult.Errors)
        {
            errors.Add(new LoadError
            {
                RowNumber = bulkError.Index + 1, // 1-based row number
                ErrorCode = LoadErrorCodes.DataverseError,
                Message = bulkError.Message
            });
        }

        stopwatch.Stop();

        return new LoadResult
        {
            TotalRows = entities.Count + parseErrors.Count,
            SuccessCount = bulkResult.SuccessCount,
            FailureCount = bulkResult.FailureCount + parseErrors.Count,
            CreatedCount = bulkResult.CreatedCount,
            UpdatedCount = bulkResult.UpdatedCount,
            SkippedCount = parseErrors.Count,
            Duration = stopwatch.Elapsed,
            Errors = errors,
            Warnings = warnings
        };
    }

    private async Task<EntityMetadata> RetrieveEntityMetadataAsync(
        string entityLogicalName,
        CancellationToken cancellationToken)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var request = new RetrieveEntityRequest
        {
            LogicalName = entityLogicalName,
            EntityFilters = EntityFilters.Attributes
        };

        var response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken);
        return response.EntityMetadata;
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
            if (attr.LogicalName != null)
            {
                lookup[attr.LogicalName] = attr;
            }
        }

        return lookup;
    }

    private async Task<AutoMappingResult> AutoMapColumnsAsync(
        string csvPath,
        string entityLogicalName,
        Dictionary<string, AttributeMetadata> attributesByName,
        CancellationToken cancellationToken)
    {
        var mappings = new Dictionary<string, ColumnMappingEntry>(StringComparer.OrdinalIgnoreCase);
        var unmatchedColumns = new List<UnmatchedColumn>();
        var warnings = new List<string>();
        var matchedCount = 0;

        // Extract publisher prefix from entity name (e.g., "ppds_city" → "ppds_")
        var prefix = ExtractPublisherPrefix(entityLogicalName);

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

        foreach (var header in headers)
        {
            var normalizedHeader = NormalizeForMatching(header);

            // Try exact match first
            if (attributesByName.TryGetValue(header, out var attr))
            {
                mappings[header] = CreateAutoMapping(header, attr);
                matchedCount++;
            }
            // Try prefix + column (e.g., "city" → "ppds_city")
            else if (prefix != null && attributesByName.TryGetValue(prefix + header, out attr))
            {
                mappings[header] = CreateAutoMapping(header, attr);
                matchedCount++;
            }
            // Try normalized prefix + column
            else if (prefix != null && TryFindAttribute(NormalizeForMatching(prefix + header), attributesByName, out attr))
            {
                mappings[header] = CreateAutoMapping(header, attr!);
                matchedCount++;
            }
            // Try normalized match (existing)
            else if (TryFindAttribute(normalizedHeader, attributesByName, out attr))
            {
                mappings[header] = CreateAutoMapping(header, attr!);
                matchedCount++;
            }
            else
            {
                // Column could not be matched - collect suggestions
                var suggestions = FindSimilarAttributes(header, attributesByName, prefix);
                unmatchedColumns.Add(new UnmatchedColumn
                {
                    ColumnName = header,
                    Suggestions = suggestions.Count > 0 ? suggestions : null
                });
                warnings.Add($"Column '{header}' does not match any attribute.");
                mappings[header] = new ColumnMappingEntry { Skip = true };
            }
        }

        return new AutoMappingResult
        {
            Mappings = mappings,
            TotalColumns = headers.Length,
            MatchedColumns = matchedCount,
            UnmatchedColumns = unmatchedColumns,
            Warnings = warnings
        };
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

    private static List<string> FindSimilarAttributes(
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
            .Take(3)
            .Select(r => r.Name)
            .ToList();
    }

    private static ColumnMappingEntry CreateAutoMapping(string header, AttributeMetadata attr)
    {
        var entry = new ColumnMappingEntry
        {
            Field = attr.LogicalName
        };

        // Auto-configure lookups with GUID-only matching
        if (IsLookupAttribute(attr))
        {
            var lookupAttr = (LookupAttributeMetadata)attr;
            entry.Lookup = new LookupConfig
            {
                Entity = lookupAttr.Targets?.FirstOrDefault() ?? "unknown",
                MatchBy = "guid"
            };
        }

        return entry;
    }

    private static bool TryFindAttribute(
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

    private static string NormalizeForMatching(string value)
    {
        return value
            .Replace(" ", "")
            .Replace("_", "")
            .Replace("-", "")
            .ToLowerInvariant();
    }

    private static IEnumerable<(string ColumnName, LookupConfig Config)> GetLookupConfigs(
        Dictionary<string, ColumnMappingEntry> mappings,
        Dictionary<string, AttributeMetadata> attributes)
    {
        foreach (var (columnName, mapping) in mappings)
        {
            if (mapping.Skip || mapping.Lookup == null)
            {
                continue;
            }

            yield return (columnName, mapping.Lookup);
        }
    }

    private async Task<(List<Entity> Entities, List<LoadError> Errors)> BuildEntitiesAsync(
        string csvPath,
        CsvLoadOptions options,
        Dictionary<string, ColumnMappingEntry> mappings,
        Dictionary<string, AttributeMetadata> attributesByName,
        LookupResolver lookupResolver,
        CancellationToken cancellationToken)
    {
        var entities = new List<Entity>();
        var errors = new List<LoadError>();
        var parser = new CsvRecordParser();

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

        var rowNumber = 0;
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;

            var entity = new Entity(options.EntityLogicalName);
            var hasError = false;
            var processedKeyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Set alternate key if specified
            if (!string.IsNullOrEmpty(options.AlternateKeyFields))
            {
                var keyFields = options.AlternateKeyFields.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var keyField in keyFields)
                {
                    var trimmedKey = keyField.Trim();
                    // Find the header that maps to this key field
                    var keyHeader = FindHeaderForField(headers, mappings, trimmedKey);
                    if (keyHeader != null)
                    {
                        var keyValue = csv.GetField(keyHeader);
                        if (!string.IsNullOrEmpty(keyValue))
                        {
                            // Coerce key value if we have metadata
                            if (attributesByName.TryGetValue(trimmedKey, out var keyAttr))
                            {
                                // Check if this key field is a lookup
                                if (IsLookupAttribute(keyAttr))
                                {
                                    var mapping = mappings.GetValueOrDefault(keyHeader);
                                    if (mapping?.Lookup != null)
                                    {
                                        var entityRef = lookupResolver.Resolve(keyValue, mapping.Lookup, rowNumber, keyHeader);
                                        if (entityRef != null)
                                        {
                                            entity.KeyAttributes[trimmedKey] = entityRef;
                                            processedKeyFields.Add(trimmedKey);
                                        }
                                        else
                                        {
                                            // Lookup resolution failed - error already added by resolver
                                            hasError = true;
                                        }
                                    }
                                    else
                                    {
                                        // Lookup key field without lookup configuration
                                        errors.Add(new LoadError
                                        {
                                            RowNumber = rowNumber,
                                            Column = keyHeader,
                                            ErrorCode = LoadErrorCodes.LookupNotResolved,
                                            Message = $"Alternate key field '{trimmedKey}' is a lookup but has no lookup configuration. " +
                                                      "Use --generate-mapping to configure lookup resolution.",
                                            Value = keyValue
                                        });
                                        hasError = true;
                                    }
                                }
                                else
                                {
                                    // Non-lookup key field - use standard coercion
                                    var coercedKey = parser.CoerceValue(keyValue, keyAttr, mappings.GetValueOrDefault(keyHeader));
                                    if (coercedKey != null)
                                    {
                                        entity.KeyAttributes[trimmedKey] = coercedKey;
                                        processedKeyFields.Add(trimmedKey);
                                    }
                                    else
                                    {
                                        // Key coercion failed - this is a critical error
                                        errors.Add(new LoadError
                                        {
                                            RowNumber = rowNumber,
                                            Column = keyHeader,
                                            ErrorCode = LoadErrorCodes.TypeCoercionFailed,
                                            Message = $"Cannot convert key value '{keyValue}' to {keyAttr.AttributeType}",
                                            Value = keyValue
                                        });
                                        hasError = true;
                                    }
                                }
                            }
                            else
                            {
                                entity.KeyAttributes[trimmedKey] = keyValue;
                                processedKeyFields.Add(trimmedKey);
                            }
                        }
                    }
                }
            }

            foreach (var header in headers)
            {
                if (!mappings.TryGetValue(header, out var mapping) || mapping.Skip)
                {
                    continue;
                }

                var fieldName = mapping.Field;
                if (string.IsNullOrEmpty(fieldName))
                {
                    continue;
                }

                // Skip fields already processed as alternate key attributes
                if (processedKeyFields.Contains(fieldName))
                {
                    continue;
                }

                var rawValue = csv.GetField(header);

                if (string.IsNullOrEmpty(rawValue))
                {
                    continue;
                }

                // Handle lookups
                if (mapping.Lookup != null)
                {
                    var entityRef = lookupResolver.Resolve(rawValue, mapping.Lookup, rowNumber, header);
                    if (entityRef != null)
                    {
                        entity[fieldName] = entityRef;
                    }
                    // Errors are collected in lookupResolver.Errors
                    continue;
                }

                // Handle regular attributes
                if (!attributesByName.TryGetValue(fieldName, out var attrMetadata))
                {
                    errors.Add(new LoadError
                    {
                        RowNumber = rowNumber,
                        Column = header,
                        ErrorCode = LoadErrorCodes.ColumnNotFound,
                        Message = $"Attribute '{fieldName}' not found in entity",
                        Value = rawValue
                    });
                    hasError = true;
                    continue;
                }

                var (success, value, errorMessage) = parser.TryCoerceValue(rawValue, attrMetadata, mapping);

                if (!success)
                {
                    errors.Add(new LoadError
                    {
                        RowNumber = rowNumber,
                        Column = header,
                        ErrorCode = LoadErrorCodes.TypeCoercionFailed,
                        Message = errorMessage ?? $"Cannot convert value",
                        Value = rawValue
                    });
                    hasError = true;
                    continue;
                }

                if (value != null)
                {
                    entity[fieldName] = value;
                }
            }

            // Only add entities without errors to the batch
            // Errors are collected and reported; ContinueOnError controls whether
            // we proceed with valid entities or abort entirely (checked by caller)
            if (!hasError)
            {
                entities.Add(entity);
            }
        }

        return (entities, errors);
    }

    private static string? FindHeaderForField(
        string[] headers,
        Dictionary<string, ColumnMappingEntry> mappings,
        string fieldName)
    {
        foreach (var header in headers)
        {
            if (mappings.TryGetValue(header, out var mapping) &&
                string.Equals(mapping.Field, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return header;
            }
        }

        // Also check if header directly matches field name
        foreach (var header in headers)
        {
            if (string.Equals(header, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return header;
            }
        }

        return null;
    }

    private async Task<MappingValidationResult> ValidateMappingFileAsync(
        string csvPath,
        Dictionary<string, ColumnMappingEntry> mappings,
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

        var csvColumns = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        var mappingColumns = new HashSet<string>(mappings.Keys, StringComparer.OrdinalIgnoreCase);

        var unconfiguredColumns = new List<string>();
        var missingMappings = new List<string>();
        var staleMappings = new List<string>();

        // Check for unconfigured columns (field: null without skip: true)
        foreach (var (columnName, mapping) in mappings)
        {
            if (string.IsNullOrEmpty(mapping.Field) && !mapping.Skip)
            {
                unconfiguredColumns.Add(columnName);
            }
        }

        // Check for CSV columns not in mapping
        foreach (var csvColumn in csvColumns)
        {
            if (!mappingColumns.Contains(csvColumn))
            {
                missingMappings.Add(csvColumn);
            }
        }

        // Check for mapping columns not in CSV (stale entries - warning only)
        foreach (var mappingColumn in mappingColumns)
        {
            if (!csvColumns.Contains(mappingColumn))
            {
                staleMappings.Add(mappingColumn);
            }
        }

        return new MappingValidationResult
        {
            UnconfiguredColumns = unconfiguredColumns,
            MissingMappings = missingMappings,
            StaleMappings = staleMappings
        };
    }

    private static bool IsLookupAttribute(AttributeMetadata attr)
    {
        return attr.AttributeType == AttributeTypeCode.Lookup ||
               attr.AttributeType == AttributeTypeCode.Customer ||
               attr.AttributeType == AttributeTypeCode.Owner;
    }
}

/// <summary>
/// Result of validating a mapping file against a CSV.
/// </summary>
internal sealed record MappingValidationResult
{
    /// <summary>
    /// Columns that have no field configured and are not marked as skip.
    /// </summary>
    public required List<string> UnconfiguredColumns { get; init; }

    /// <summary>
    /// CSV columns that are not present in the mapping file.
    /// </summary>
    public required List<string> MissingMappings { get; init; }

    /// <summary>
    /// Mapping columns that are not present in the CSV (stale entries).
    /// </summary>
    public required List<string> StaleMappings { get; init; }
}
