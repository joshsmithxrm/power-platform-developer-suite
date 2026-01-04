namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Thrown when a mapping file fails validation.
/// </summary>
public sealed class MappingValidationException : Exception
{
    /// <summary>
    /// Columns that have no field configured and are not marked as skip.
    /// </summary>
    public IReadOnlyList<string> UnconfiguredColumns { get; }

    /// <summary>
    /// CSV columns that are not present in the mapping file.
    /// </summary>
    public IReadOnlyList<string> MissingMappings { get; }

    /// <summary>
    /// Mapping columns that are not present in the CSV (stale entries - warning only).
    /// </summary>
    public IReadOnlyList<string> StaleMappings { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MappingValidationException"/> class.
    /// </summary>
    public MappingValidationException(
        IReadOnlyList<string> unconfiguredColumns,
        IReadOnlyList<string> missingMappings,
        IReadOnlyList<string> staleMappings)
        : base(BuildMessage(unconfiguredColumns, missingMappings))
    {
        UnconfiguredColumns = unconfiguredColumns;
        MissingMappings = missingMappings;
        StaleMappings = staleMappings;
    }

    private static string BuildMessage(
        IReadOnlyList<string> unconfigured,
        IReadOnlyList<string> missing)
    {
        var issues = new List<string>();

        if (unconfigured.Count > 0)
        {
            issues.Add($"{unconfigured.Count} column(s) have no field configured (set 'field' or 'skip: true')");
        }

        if (missing.Count > 0)
        {
            issues.Add($"{missing.Count} CSV column(s) not found in mapping file");
        }

        return $"Mapping file validation failed: {string.Join("; ", issues)}";
    }
}
