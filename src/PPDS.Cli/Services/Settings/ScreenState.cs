namespace PPDS.Cli.Services.Settings;

/// <summary>
/// Persisted state for WebResourcesScreen.
/// </summary>
internal sealed record WebResourcesScreenState
{
    public Guid? SelectedSolutionId { get; init; }
    public bool TextOnly { get; init; } = true;
}

/// <summary>
/// Persisted state for screens with a solution name filter (ConnectionReferences, EnvironmentVariables).
/// </summary>
internal sealed record SolutionFilterScreenState
{
    public string? SolutionFilter { get; init; }
}
