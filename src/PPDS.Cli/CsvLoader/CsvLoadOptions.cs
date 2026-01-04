using PPDS.Dataverse.BulkOperations;

namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Options for CSV data loading operations.
/// </summary>
public sealed class CsvLoadOptions
{
    /// <summary>
    /// Target entity logical name.
    /// </summary>
    public required string EntityLogicalName { get; init; }

    /// <summary>
    /// Alternate key field(s) for upsert operations. Comma-separated for composite keys.
    /// </summary>
    public string? AlternateKeyFields { get; init; }

    /// <summary>
    /// Column mapping configuration. If null, auto-mapping is used.
    /// </summary>
    public CsvMappingConfig? Mapping { get; init; }

    /// <summary>
    /// Number of records per batch.
    /// </summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>
    /// Bypass custom plugin execution.
    /// </summary>
    public CustomLogicBypass BypassPlugins { get; init; } = CustomLogicBypass.None;

    /// <summary>
    /// Bypass Power Automate flow triggers.
    /// </summary>
    public bool BypassFlows { get; init; }

    /// <summary>
    /// Continue loading on individual record failures.
    /// </summary>
    public bool ContinueOnError { get; init; } = true;

    /// <summary>
    /// Validate without writing to Dataverse.
    /// </summary>
    public bool DryRun { get; init; }
}
