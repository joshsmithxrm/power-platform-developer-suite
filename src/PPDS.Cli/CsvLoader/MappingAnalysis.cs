namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Complete analysis of CSV-to-entity mapping.
/// </summary>
public sealed record MappingAnalysis
{
    /// <summary>
    /// Target entity logical name.
    /// </summary>
    public required string Entity { get; init; }

    /// <summary>
    /// Total number of columns in the CSV.
    /// </summary>
    public required int TotalColumns { get; init; }

    /// <summary>
    /// Number of columns that were matched.
    /// </summary>
    public required int MatchedColumns { get; init; }

    /// <summary>
    /// Match rate as a decimal (0.0 to 1.0).
    /// </summary>
    public double MatchRate => TotalColumns > 0
        ? (double)MatchedColumns / TotalColumns
        : 0.0;

    /// <summary>
    /// Whether all columns were matched.
    /// </summary>
    public bool IsComplete => MatchedColumns == TotalColumns;

    /// <summary>
    /// Publisher prefix extracted from entity name.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// Per-column analysis results.
    /// </summary>
    public required List<ColumnAnalysis> Columns { get; init; }

    /// <summary>
    /// Recommendations for the user.
    /// </summary>
    public required List<string> Recommendations { get; init; }
}

/// <summary>
/// Analysis of a single CSV column.
/// </summary>
public sealed record ColumnAnalysis
{
    /// <summary>
    /// The CSV column header name.
    /// </summary>
    public required string CsvColumn { get; init; }

    /// <summary>
    /// Whether the column was matched to an attribute.
    /// </summary>
    public required bool IsMatched { get; init; }

    /// <summary>
    /// The target Dataverse attribute logical name (if matched).
    /// </summary>
    public string? TargetAttribute { get; init; }

    /// <summary>
    /// How the match was made (exact, prefix, normalized).
    /// </summary>
    public string? MatchType { get; init; }

    /// <summary>
    /// The attribute type (if matched).
    /// </summary>
    public string? AttributeType { get; init; }

    /// <summary>
    /// Whether this is a lookup field requiring configuration.
    /// </summary>
    public bool IsLookup { get; init; }

    /// <summary>
    /// Similar attribute names (if not matched).
    /// </summary>
    public List<string>? Suggestions { get; init; }

    /// <summary>
    /// Sample values from the CSV.
    /// </summary>
    public List<string>? SampleValues { get; init; }
}
