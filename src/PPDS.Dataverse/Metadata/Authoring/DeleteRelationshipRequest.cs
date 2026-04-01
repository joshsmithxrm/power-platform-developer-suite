namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to delete a Dataverse relationship.
/// </summary>
public sealed class DeleteRelationshipRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the relationship.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the schema name of the relationship to delete.</summary>
    public string SchemaName { get; set; } = "";

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
