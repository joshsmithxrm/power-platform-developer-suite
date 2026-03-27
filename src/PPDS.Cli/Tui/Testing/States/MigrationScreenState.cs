namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the MigrationScreen for testing.
/// </summary>
/// <param name="Mode">"Export" or "Import".</param>
/// <param name="OperationState">"Idle", "Configuring", "Running", "Completed", "Failed", or "Cancelled".</param>
/// <param name="SchemaPath">Export: schema file path.</param>
/// <param name="OutputPath">Export: output file path.</param>
/// <param name="DataPath">Import: data file path.</param>
/// <param name="CurrentPhase">Current migration phase label.</param>
/// <param name="CurrentEntity">Current entity being processed.</param>
/// <param name="RecordCount">Total records processed.</param>
/// <param name="SuccessCount">Successful records.</param>
/// <param name="FailureCount">Failed records.</param>
/// <param name="WarningCount">Warning count.</param>
/// <param name="SkipCount">Skipped records.</param>
/// <param name="RecordsPerSecond">Processing rate.</param>
/// <param name="EstimatedRemaining">ETA string.</param>
/// <param name="Elapsed">Elapsed time string.</param>
/// <param name="ErrorMessage">Error message if failed (null if no error).</param>
/// <param name="IsLoading">Whether operation is running.</param>
/// <param name="StatusMessage">Current status bar message.</param>
public sealed record MigrationScreenState(
    string Mode,
    string OperationState,
    string? SchemaPath,
    string? OutputPath,
    string? DataPath,
    string? CurrentPhase,
    string? CurrentEntity,
    int RecordCount,
    int SuccessCount,
    int FailureCount,
    int WarningCount,
    int SkipCount,
    double? RecordsPerSecond,
    string? EstimatedRemaining,
    string? Elapsed,
    string? ErrorMessage,
    bool IsLoading,
    string? StatusMessage);
