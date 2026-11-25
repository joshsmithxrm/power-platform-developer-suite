using System.Diagnostics;
using System.Globalization;
using System.ServiceModel;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Services.Data;
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
    private readonly IBulkOperationExecutor? _bulkExecutor;
    private readonly ILogger<CsvDataLoader>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CsvDataLoader"/> class.
    /// </summary>
    /// <param name="pool">Connection pool for Dataverse operations.</param>
    /// <param name="bulkExecutor">Bulk operation executor. Required for LoadAsync, optional for AnalyzeAsync.</param>
    /// <param name="logger">Optional logger.</param>
    public CsvDataLoader(
        IDataverseConnectionPool pool,
        IBulkOperationExecutor? bulkExecutor,
        ILogger<CsvDataLoader>? logger = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _bulkExecutor = bulkExecutor;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes CSV-to-entity mapping without loading data.
    /// </summary>
    public async Task<MappingAnalysis> AnalyzeAsync(
        string csvPath,
        string entityLogicalName,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Analyzing CSV file: {CsvPath}", csvPath);

        // Retrieve entity metadata
        var entityMetadata = await RetrieveEntityMetadataAsync(entityLogicalName, cancellationToken);
        var attributesByName = ColumnMatcher.BuildAttributeLookup(entityMetadata);

        // Extract publisher prefix
        var prefix = ColumnMatcher.ExtractPublisherPrefix(entityLogicalName);

        // Read headers and sample values
        var (headers, sampleValues) = await ReadCsvHeadersAndSamplesAsync(csvPath, cancellationToken);

        var columns = new List<ColumnAnalysis>();
        var matchedCount = 0;
        var recommendations = new List<string>();

        foreach (var header in headers)
        {
            var samples = sampleValues.GetValueOrDefault(header);
            var analysis = AnalyzeColumn(header, attributesByName, prefix, samples);
            columns.Add(analysis);

            if (analysis.IsMatched)
            {
                matchedCount++;
            }
        }

        // Generate recommendations
        var unmatchedCount = headers.Length - matchedCount;
        var lookupCount = columns.Count(c => c.IsLookup);

        if (unmatchedCount > 0)
        {
            recommendations.Add($"Use --generate-mapping to create a mapping file for the {unmatchedCount} unmatched column(s).");
        }

        if (lookupCount > 0)
        {
            recommendations.Add($"{lookupCount} lookup field(s) detected. Review generated mapping to configure resolution.");
        }

        if (unmatchedCount == 0 && lookupCount == 0)
        {
            recommendations.Add($"All columns matched. Ready to load with: ppds data load --entity {entityLogicalName} --file {Path.GetFileName(csvPath)}");
        }

        return new MappingAnalysis
        {
            Entity = entityLogicalName,
            TotalColumns = headers.Length,
            MatchedColumns = matchedCount,
            Prefix = prefix,
            Columns = columns,
            Recommendations = recommendations
        };
    }

    private async Task<(string[] Headers, Dictionary<string, List<string>> SampleValues)> ReadCsvHeadersAndSamplesAsync(
        string csvPath,
        CancellationToken cancellationToken)
    {
        var maxSampleValues = ColumnMatcher.MaxSampleValues;

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

        // Collect sample values
        var sampleValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            sampleValues[header] = new List<string>();
        }

        var rowCount = 0;
        while (await csv.ReadAsync() && rowCount < maxSampleValues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var header in headers)
            {
                var value = csv.GetField(header);
                if (!string.IsNullOrEmpty(value)
                    && sampleValues[header].Count < maxSampleValues
                    && !sampleValues[header].Contains(value))
                {
                    sampleValues[header].Add(value);
                }
            }
            rowCount++;
        }

        return (headers, sampleValues);
    }

    private ColumnAnalysis AnalyzeColumn(
        string header,
        Dictionary<string, AttributeMetadata> attributesByName,
        string? prefix,
        List<string>? samples)
    {
        // Try exact match first
        if (attributesByName.TryGetValue(header, out var attr))
        {
            return CreateMatchedAnalysis(header, attr, "exact", samples);
        }

        // Try prefix + column
        if (prefix != null && attributesByName.TryGetValue(prefix + header, out attr))
        {
            return CreateMatchedAnalysis(header, attr, "prefix", samples);
        }

        // Try normalized prefix + column
        if (prefix != null && ColumnMatcher.TryFindAttribute(ColumnMatcher.NormalizeForMatching(prefix + header), attributesByName, out attr))
        {
            return CreateMatchedAnalysis(header, attr!, "normalized-prefix", samples);
        }

        // Try normalized match
        if (ColumnMatcher.TryFindAttribute(ColumnMatcher.NormalizeForMatching(header), attributesByName, out attr))
        {
            return CreateMatchedAnalysis(header, attr!, "normalized", samples);
        }

        // No match
        var suggestions = ColumnMatcher.FindSimilarAttributes(header, attributesByName, prefix);
        return new ColumnAnalysis
        {
            CsvColumn = header,
            IsMatched = false,
            Suggestions = suggestions.Count > 0 ? suggestions : null,
            SampleValues = samples?.Count > 0 ? samples : null
        };
    }

    private static ColumnAnalysis CreateMatchedAnalysis(
        string header,
        AttributeMetadata attr,
        string matchType,
        List<string>? samples)
    {
        var isLookup = ColumnMatcher.IsLookupAttribute(attr);
        return new ColumnAnalysis
        {
            CsvColumn = header,
            IsMatched = true,
            TargetAttribute = attr.LogicalName,
            MatchType = matchType,
            AttributeType = attr.AttributeType?.ToString(),
            IsLookup = isLookup,
            SampleValues = samples?.Count > 0 ? samples : null
        };
    }

    /// <summary>
    /// Loads CSV data into Dataverse. The whole file is read and checked before anything is written (#1916): the file is a
    /// well-formed CSV with the header's number of fields in every row (<see cref="StrictCsvFile"/>), every mapped column
    /// and lookup configuration fits the table's metadata, and every cell is exact for its column. Anything else throws
    /// <see cref="DataValuesRefusedException"/> listing each row and column, and nothing is written.
    /// </summary>
    /// <exception cref="DataValuesRefusedException">The file, the mapping or a cell is refused; nothing was written.</exception>
    public async Task<LoadResult> LoadAsync(
        string csvPath,
        CsvLoadOptions options,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_bulkExecutor == null)
        {
            throw new InvalidOperationException(
                "BulkExecutor is required for LoadAsync. Use the constructor overload that provides IBulkOperationExecutor.");
        }

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        _logger?.LogInformation("Loading CSV file: {CsvPath}", csvPath);

        // 1. Retrieve entity metadata
        var entityMetadata = await RetrieveEntityMetadataAsync(
            options.EntityLogicalName, cancellationToken);

        // 2. Build attribute lookup
        var attributesByName = ColumnMatcher.BuildAttributeLookup(entityMetadata);

        // 3. Read the whole file exactly: header, quoting and field counts. Refused here before anything else.
        var table = await StrictCsvFile.ReadAsync(csvPath, cancellationToken);
        var headers = table.Headers.ToArray();

        // 4. Determine column mappings
        Dictionary<string, ColumnMappingEntry> mappings;
        if (options.Mapping?.Columns != null)
        {
            // Headers are matched to mapping keys ignoring case everywhere, so the keys are too, and two keys that differ
            // only in case are refused: which entry is meant is not certain.
            mappings = new Dictionary<string, ColumnMappingEntry>(StringComparer.OrdinalIgnoreCase);
            var keyProblems = new List<DataValueProblem>();
            foreach (var (name, entry) in options.Mapping.Columns)
            {
                if (!mappings.TryAdd(name, entry))
                {
                    keyProblems.Add(new DataValueProblem(null, name,
                        "The mapping file has more than one entry for this column (ignoring case), so which is meant is not certain."));
                }
            }

            if (keyProblems.Count > 0)
            {
                throw new DataValuesRefusedException(keyProblems);
            }

            // Validate the mapping file against the CSV
            var validationResult = ValidateMapping(headers, mappings);

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
            var ambiguous = new List<DataValueProblem>();
            var autoMappingResult = AutoMapColumns(headers, options.EntityLogicalName, attributesByName, ambiguous);
            if (ambiguous.Count > 0)
            {
                throw new DataValuesRefusedException(ambiguous);
            }

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

        // 5. The mapped columns and lookup configurations fit the table, before any lookup is read.
        var problems = CheckMappings(headers, mappings, attributesByName, options.AlternateKeyFields);
        problems.AddRange(await CheckLookupKeyFieldsAsync(headers, mappings, cancellationToken));
        if (problems.Count > 0)
        {
            throw new DataValuesRefusedException(problems);
        }

        // 6. Identify and preload lookup caches
        var lookupResolver = new LookupResolver(_pool);
        var lookupConfigs = GetLookupConfigs(mappings, attributesByName);

        if (lookupConfigs.Any(l => l.Config.MatchBy == "field"))
        {
            _logger?.LogInformation("Preloading lookup caches...");
            await lookupResolver.PreloadLookupsAsync(lookupConfigs, cancellationToken);
        }

        // 7. Convert every cell; any problem refuses the whole file and nothing is written.
        var entities = BuildEntities(table, options, mappings, attributesByName, lookupResolver, problems, cancellationToken);
        problems.AddRange(lookupResolver.Errors.Select(e => new DataValueProblem(e.RowNumber, e.Column, e.Message)));
        if (problems.Count > 0)
        {
            throw new DataValuesRefusedException(problems
                .OrderBy(p => p.Row ?? 0)
                .ThenBy(p => p.Column is null ? -1 : Array.FindIndex(headers, h => h == p.Column))
                .ToList());
        }

        _logger?.LogInformation("Built {Count} entities from CSV", entities.Count);

        // 8. Dry-run mode - just return validation results
        if (options.DryRun)
        {
            stopwatch.Stop();
            return new LoadResult
            {
                TotalRows = entities.Count,
                SuccessCount = entities.Count,
                FailureCount = 0,
                SkippedCount = 0,
                Duration = stopwatch.Elapsed,
                Warnings = warnings
            };
        }

        // 9. Execute bulk upsert
        if (entities.Count == 0)
        {
            stopwatch.Stop();
            return new LoadResult
            {
                TotalRows = 0,
                SuccessCount = 0,
                FailureCount = 0,
                Duration = stopwatch.Elapsed,
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
            progress: progress,
            cancellationToken: cancellationToken);

        // 10. Map bulk operation errors
        var errors = bulkResult.Errors.Select(bulkError => new LoadError
        {
            RowNumber = bulkError.Index + 1, // 1-based row number
            ErrorCode = LoadErrorCodes.DataverseError,
            Message = bulkError.Message
        }).ToList();

        stopwatch.Stop();

        return new LoadResult
        {
            TotalRows = entities.Count,
            SuccessCount = bulkResult.SuccessCount,
            FailureCount = bulkResult.FailureCount,
            CreatedCount = bulkResult.CreatedCount,
            UpdatedCount = bulkResult.UpdatedCount,
            SkippedCount = 0,
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

    private static AutoMappingResult AutoMapColumns(
        string[] headers,
        string entityLogicalName,
        Dictionary<string, AttributeMetadata> attributesByName,
        List<DataValueProblem> ambiguous)
    {
        var mappings = new Dictionary<string, ColumnMappingEntry>(StringComparer.OrdinalIgnoreCase);
        var unmatchedColumns = new List<UnmatchedColumn>();
        var warnings = new List<string>();
        var matchedCount = 0;

        // Extract publisher prefix from entity name (e.g., "ppds_city" → "ppds_")
        var prefix = ColumnMatcher.ExtractPublisherPrefix(entityLogicalName);

        foreach (var header in headers)
        {
            var normalizedHeader = ColumnMatcher.NormalizeForMatching(header);

            // A match by normalised name takes the one column that normalises alike, never the first of several.
            bool Normalized(string normalized, out AttributeMetadata? found)
            {
                var candidates = attributesByName.Values
                    .Where(a => ColumnMatcher.NormalizeForMatching(a.LogicalName) == normalized)
                    .DistinctBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                found = candidates.Count == 1 ? candidates[0] : null;
                if (candidates.Count > 1)
                {
                    ambiguous.Add(new DataValueProblem(null, header,
                        $"The column's name matches {candidates.Count} columns of the table ({string.Join(", ", candidates.Select(c => c.LogicalName))}) " +
                        "when compared without case, spaces, underscores and hyphens; map it in a mapping file."));
                    found = candidates[0]; // refused above; kept only so the header is not reported as unmatched too
                }

                return found != null;
            }

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
            else if (prefix != null && Normalized(ColumnMatcher.NormalizeForMatching(prefix + header), out attr))
            {
                mappings[header] = CreateAutoMapping(header, attr!);
                matchedCount++;
            }
            // Try normalized match (existing)
            else if (Normalized(normalizedHeader, out attr))
            {
                mappings[header] = CreateAutoMapping(header, attr!);
                matchedCount++;
            }
            else
            {
                // Column could not be matched - collect suggestions
                var suggestions = ColumnMatcher.FindSimilarAttributes(header, attributesByName, prefix);
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

    private static ColumnMappingEntry CreateAutoMapping(string header, AttributeMetadata attr)
    {
        var entry = new ColumnMappingEntry
        {
            Field = attr.LogicalName
        };

        // Auto-configure a lookup to exactly one table with GUID matching. A lookup that can point to several tables
        // (customer, owner, regarding) gets no table: a GUID does not say which, so the mapping file must name it.
        if (ColumnMatcher.IsLookupAttribute(attr) && ((LookupAttributeMetadata)attr).Targets is [var target]
            && !string.IsNullOrEmpty(target))
        {
            entry.Lookup = new LookupConfig
            {
                Entity = target,
                MatchBy = "guid"
            };
        }

        return entry;
    }

    /// <summary>
    /// Checks the mapped columns against the table before any lookup or cell is read: each maps to a column of the table,
    /// no two map to the same column, a lookup configuration names one of its column's target tables and a known way to
    /// match, a lookup to several tables has a configuration, and at least one column is mapped.
    /// </summary>
    private static List<DataValueProblem> CheckMappings(
        string[] headers,
        Dictionary<string, ColumnMappingEntry> mappings,
        Dictionary<string, AttributeMetadata> attributesByName,
        string? alternateKeyFields)
    {
        var problems = new List<DataValueProblem>();
        var mappedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (!mappings.TryGetValue(header, out var mapping) || mapping.Skip || string.IsNullOrEmpty(mapping.Field))
            {
                continue;
            }

            if (mappedFields.TryGetValue(mapping.Field, out var other))
            {
                problems.Add(new DataValueProblem(null, header,
                    $"The column maps to '{mapping.Field}', which column '{other}' also maps to."));
                continue;
            }

            mappedFields[mapping.Field] = header;
            if (!attributesByName.TryGetValue(mapping.Field, out var attr))
            {
                problems.Add(new DataValueProblem(null, header, $"'{mapping.Field}' is not a column of the table."));
                continue;
            }

            // An upsert creates the record or updates it, and which is not known before it is sent. A column that takes
            // no value in one of the two (createdon; a primary id column, valid for create only; statecode in many tables,
            // valid for update only) is refused: what Dataverse does with the value then is not certain.
            if (attr.IsValidForCreate == false || attr.IsValidForUpdate == false)
            {
                var takes = (attr.IsValidForCreate, attr.IsValidForUpdate) switch
                {
                    (false, false) => "takes no value when a record is created or updated",
                    (false, _) => "takes no value when a record is created",
                    _ => "takes no value when a record is updated",
                };
                problems.Add(new DataValueProblem(null, header,
                    $"'{attr.LogicalName}' {takes}, and a load may do either; skip the column in a mapping file."));
                continue;
            }

            if (!string.IsNullOrEmpty(mapping.DateFormat) && attr is DateTimeAttributeMetadata
                && ColumnValueConverter.DateFormatRefusal(mapping.DateFormat) is { } badFormat)
            {
                problems.Add(new DataValueProblem(null, header, badFormat));
                continue;
            }

            var isLookup = ColumnMatcher.IsLookupAttribute(attr);
            var targets = (attr as LookupAttributeMetadata)?.Targets ?? [];
            if (mapping.Lookup is { } lookup)
            {
                if (!isLookup)
                {
                    problems.Add(new DataValueProblem(null, header,
                        $"The mapping gives a lookup configuration, but '{attr.LogicalName}' is not a lookup column."));
                }
                else if (!targets.Contains(lookup.Entity, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(new DataValueProblem(null, header,
                        $"The mapping's lookup table is not one this column points to ({string.Join(", ", targets)}); " +
                        "set lookup.entity to one of them."));
                }
                else if (lookup.MatchBy != "guid" && !(lookup.MatchBy == "field" && !string.IsNullOrEmpty(lookup.KeyField)))
                {
                    problems.Add(new DataValueProblem(null, header,
                        "The lookup's matchBy is neither 'guid' nor 'field' with a keyField."));
                }
            }
            else if (isLookup && targets.Length != 1)
            {
                problems.Add(new DataValueProblem(null, header,
                    $"The lookup can point to several tables ({string.Join(", ", targets)}) and a GUID does not say which; " +
                    "name the table in a mapping file (lookup.entity), or skip the column."));
            }
        }

        if (mappedFields.Count == 0)
        {
            problems.Add(new DataValueProblem(null, null,
                "No column of the file maps to a column of the table, so every record would be empty. Check the delimiter " +
                "(a comma) and the header."));
        }

        if (!string.IsNullOrEmpty(alternateKeyFields))
        {
            foreach (var keyField in alternateKeyFields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()))
            {
                if (!attributesByName.ContainsKey(keyField))
                {
                    problems.Add(new DataValueProblem(null, FindHeaderForField(headers, mappings, keyField),
                        $"The alternate key field '{keyField}' is not a column of the table."));
                }
                else if (FindHeaderForField(headers, mappings, keyField) == null)
                {
                    problems.Add(new DataValueProblem(null, null,
                        $"The alternate key field '{keyField}' is not a column of the file."));
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// A lookup matched by a key field compares the cell's text with the key field's text, so the key field must be a text
    /// column of the lookup's table: any other value would be turned into text in the machine's culture (#1916). Each
    /// table and key field is read once from metadata.
    /// </summary>
    private async Task<List<DataValueProblem>> CheckLookupKeyFieldsAsync(
        string[] headers,
        Dictionary<string, ColumnMappingEntry> mappings,
        CancellationToken cancellationToken)
    {
        var problems = new List<DataValueProblem>();
        var refusals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (!mappings.TryGetValue(header, out var mapping) || mapping.Skip || string.IsNullOrEmpty(mapping.Field)
                || mapping.Lookup is not { MatchBy: "field", KeyField: { Length: > 0 } keyField } lookup)
            {
                continue;
            }

            var key = $"{lookup.Entity}:{keyField}";
            if (!refusals.TryGetValue(key, out var refusal))
            {
                refusal = await KeyFieldRefusalAsync(lookup.Entity, keyField, cancellationToken);
                refusals[key] = refusal;
            }

            if (refusal != null)
            {
                problems.Add(new DataValueProblem(null, header, refusal));
            }
        }

        return problems;
    }

    private async Task<string?> KeyFieldRefusalAsync(string entity, string keyField, CancellationToken cancellationToken)
    {
        AttributeMetadata? attribute;
        await using (var client = await _pool.GetClientAsync(cancellationToken: cancellationToken))
        {
            try
            {
                var response = (RetrieveAttributeResponse)await client.ExecuteAsync(
                    new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = keyField }, cancellationToken);
                attribute = response.AttributeMetadata;
            }
            catch (FaultException<OrganizationServiceFault>)
            {
                return $"The lookup's keyField '{keyField}' is not a column of table '{entity}'.";
            }
        }

        return attribute is StringAttributeMetadata or MemoAttributeMetadata
            ? null
            : $"The lookup's keyField '{keyField}' is not a text column of table '{entity}'. A lookup matched by field " +
              "compares text, and another type would be read in the machine's culture; match by a text column, or by GUID.";
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

    private static List<Entity> BuildEntities(
        StrictCsvFileTable table,
        CsvLoadOptions options,
        Dictionary<string, ColumnMappingEntry> mappings,
        Dictionary<string, AttributeMetadata> attributesByName,
        LookupResolver lookupResolver,
        List<DataValueProblem> problems,
        CancellationToken cancellationToken)
    {
        var entities = new List<Entity>();
        var parser = new CsvRecordParser();
        var headers = table.Headers.ToArray();
        var keyFields = string.IsNullOrEmpty(options.AlternateKeyFields)
            ? []
            : options.AlternateKeyFields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).ToArray();

        for (var index = 0; index < table.Rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowNumber = index + 1;
            var row = table.Rows[index];
            string Cell(string header) => row[Array.IndexOf(headers, header)];

            var entity = new Entity(options.EntityLogicalName);
            var hasError = false;
            var processedKeyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Set alternate key if specified (CheckMappings has refused a key field that is not a column)
            foreach (var keyField in keyFields)
            {
                var keyHeader = FindHeaderForField(headers, mappings, keyField);
                if (keyHeader == null || !attributesByName.TryGetValue(keyField, out var keyAttr))
                {
                    continue;
                }

                var keyValue = Cell(keyHeader);
                if (string.IsNullOrEmpty(keyValue))
                {
                    // Without every key value the upsert has no key, or a different one: the row is refused.
                    problems.Add(new DataValueProblem(rowNumber, keyHeader, $"The row has no value for alternate key field '{keyField}'."));
                    hasError = true;
                    continue;
                }

                processedKeyFields.Add(keyField); // reported once, as the key
                if (ColumnMatcher.IsLookupAttribute(keyAttr))
                {
                    var mapping = mappings.GetValueOrDefault(keyHeader);
                    if (mapping?.Lookup == null)
                    {
                        problems.Add(new DataValueProblem(rowNumber, keyHeader,
                            $"Alternate key field '{keyField}' is a lookup but has no lookup configuration. " +
                            "Use --generate-mapping to configure lookup resolution."));
                        hasError = true;
                        continue;
                    }

                    var entityRef = lookupResolver.Resolve(keyValue, mapping.Lookup, rowNumber, keyHeader);
                    if (entityRef != null)
                    {
                        entity.KeyAttributes[keyField] = entityRef;
                    }
                    else
                    {
                        hasError = true; // the reason is in lookupResolver.Errors
                    }

                    continue;
                }

                // Non-lookup key field: the same conversion as every other cell (#1916)
                var (keyConverted, coercedKey, keyRefusal) = parser.TryCoerceValue(keyValue, keyAttr, mappings.GetValueOrDefault(keyHeader));
                if (keyConverted && coercedKey != null)
                {
                    entity.KeyAttributes[keyField] = coercedKey;
                }
                else
                {
                    problems.Add(new DataValueProblem(rowNumber, keyHeader, $"Alternate key '{keyField}': {keyRefusal}"));
                    hasError = true;
                }
            }

            foreach (var header in headers)
            {
                if (!mappings.TryGetValue(header, out var mapping) || mapping.Skip || string.IsNullOrEmpty(mapping.Field))
                {
                    continue;
                }

                var fieldName = mapping.Field;

                // Skip fields already processed as alternate key attributes
                if (processedKeyFields.Contains(fieldName))
                {
                    continue;
                }

                var rawValue = Cell(header);
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
                    else
                    {
                        // The reason is in lookupResolver.Errors. The row is not written without the lookup it names.
                        hasError = true;
                    }

                    continue;
                }

                // CheckMappings has refused a mapped field that is not a column.
                var attrMetadata = attributesByName[fieldName];
                var (success, value, errorMessage) = parser.TryCoerceValue(rawValue, attrMetadata, mapping);
                if (!success)
                {
                    problems.Add(new DataValueProblem(rowNumber, header, errorMessage ?? "The value is not valid for the column."));
                    hasError = true;
                    continue;
                }

                if (value != null)
                {
                    entity[fieldName] = value;
                }
            }

            if (hasError)
            {
                continue;
            }

            if (entity.Attributes.Count == 0 && entity.KeyAttributes.Count == 0)
            {
                // An upsert of nothing creates an empty record.
                problems.Add(new DataValueProblem(rowNumber, null, "The row has no value for any mapped column, so it would create an empty record."));
                continue;
            }

            entities.Add(entity);
        }

        return entities;
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

    private static MappingValidationResult ValidateMapping(
        string[] headers,
        Dictionary<string, ColumnMappingEntry> mappings)
    {
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
