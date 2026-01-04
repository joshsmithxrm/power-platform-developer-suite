namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Result of auto-mapping CSV columns to entity attributes.
/// </summary>
public sealed record AutoMappingResult
{
    /// <summary>
    /// Column mappings keyed by CSV header name.
    /// </summary>
    public required Dictionary<string, ColumnMappingEntry> Mappings { get; init; }

    /// <summary>
    /// Total number of columns in the CSV.
    /// </summary>
    public required int TotalColumns { get; init; }

    /// <summary>
    /// Number of columns that were successfully matched.
    /// </summary>
    public required int MatchedColumns { get; init; }

    /// <summary>
    /// Details about columns that could not be matched.
    /// </summary>
    public required List<UnmatchedColumn> UnmatchedColumns { get; init; }

    /// <summary>
    /// Warnings generated during auto-mapping.
    /// </summary>
    public required List<string> Warnings { get; init; }

    /// <summary>
    /// Match rate as a decimal (0.0 to 1.0).
    /// </summary>
    public double MatchRate => TotalColumns > 0
        ? (double)MatchedColumns / TotalColumns
        : 0.0;

    /// <summary>
    /// Returns true if all columns were matched.
    /// </summary>
    public bool IsComplete => UnmatchedColumns.Count == 0;
}

/// <summary>
/// Details about a column that could not be auto-mapped.
/// </summary>
public sealed record UnmatchedColumn
{
    /// <summary>
    /// The CSV column header name.
    /// </summary>
    public required string ColumnName { get; init; }

    /// <summary>
    /// Similar attribute names that might be what the user intended.
    /// </summary>
    public List<string>? Suggestions { get; init; }
}
