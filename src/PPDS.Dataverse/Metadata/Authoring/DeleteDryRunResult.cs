namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Result of a delete dry-run operation, listing dependencies that would block deletion.
/// </summary>
public sealed class DeleteDryRunResult
{
    /// <summary>Gets or sets the dependency details.</summary>
    public DependencyInfo[] Dependencies { get; set; } = [];

    /// <summary>Gets or sets the total number of dependencies found.</summary>
    public int DependencyCount { get; set; }
}
