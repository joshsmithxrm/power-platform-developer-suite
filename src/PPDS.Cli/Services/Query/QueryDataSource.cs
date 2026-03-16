namespace PPDS.Cli.Services.Query;

/// <summary>
/// Identifies an environment that contributed data to a query result.
/// </summary>
public sealed record QueryDataSource
{
    /// <summary>Display label for the environment.</summary>
    public required string Label { get; init; }

    /// <summary>Whether this is a remote environment (vs the local/primary one).</summary>
    public bool IsRemote { get; init; }
}
