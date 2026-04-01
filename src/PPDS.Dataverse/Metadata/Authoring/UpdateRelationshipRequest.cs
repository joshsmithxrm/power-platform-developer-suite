namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to update an existing Dataverse relationship.
/// </summary>
public sealed class UpdateRelationshipRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the relationship.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the schema name of the relationship to update.</summary>
    public string SchemaName { get; set; } = "";

    /// <summary>Gets or sets the updated cascade configuration.</summary>
    public CascadeConfigurationDto? CascadeConfiguration { get; set; }

    /// <summary>Gets or sets whether this is a hierarchical relationship.</summary>
    public bool? IsHierarchical { get; set; }

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
