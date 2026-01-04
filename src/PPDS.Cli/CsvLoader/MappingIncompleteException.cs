namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Thrown when auto-mapping cannot match all CSV columns to entity attributes.
/// </summary>
public sealed class MappingIncompleteException : Exception
{
    /// <summary>
    /// Number of columns that were matched.
    /// </summary>
    public int MatchedColumns { get; }

    /// <summary>
    /// Total number of columns in the CSV.
    /// </summary>
    public int TotalColumns { get; }

    /// <summary>
    /// Details about unmatched columns.
    /// </summary>
    public IReadOnlyList<UnmatchedColumn> UnmatchedColumns { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MappingIncompleteException"/> class.
    /// </summary>
    public MappingIncompleteException(
        int matchedColumns,
        int totalColumns,
        IReadOnlyList<UnmatchedColumn> unmatchedColumns)
        : base(BuildMessage(matchedColumns, totalColumns, unmatchedColumns))
    {
        MatchedColumns = matchedColumns;
        TotalColumns = totalColumns;
        UnmatchedColumns = unmatchedColumns;
    }

    private static string BuildMessage(
        int matched,
        int total,
        IReadOnlyList<UnmatchedColumn> unmatched)
    {
        var unmatchedCount = unmatched.Count;
        return $"Auto-mapping incomplete: {matched}/{total} columns matched. " +
               $"{unmatchedCount} column(s) could not be mapped. " +
               "Use --generate-mapping to create a mapping file, or --force to skip unmatched columns.";
    }
}
