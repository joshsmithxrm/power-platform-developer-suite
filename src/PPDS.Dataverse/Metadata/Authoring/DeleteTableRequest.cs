namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to delete a Dataverse table (entity).
/// </summary>
public sealed class DeleteTableRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the table.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the entity to delete.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
