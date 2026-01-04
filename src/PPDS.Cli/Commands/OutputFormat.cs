namespace PPDS.Cli.Commands;

/// <summary>
/// Output format for analysis results.
/// </summary>
public enum OutputFormat
{
    /// <summary>Human-readable text output.</summary>
    Text,

    /// <summary>JSON output for programmatic consumption.</summary>
    Json,

    /// <summary>CSV output for Excel and spreadsheet tools.</summary>
    Csv
}
