namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to create a one-to-many (1:N) relationship between two Dataverse tables.
/// </summary>
public sealed class CreateOneToManyRequest
{
    /// <summary>Gets or sets the unique name of the solution to add the relationship to.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the referenced (parent / "one" side) entity.</summary>
    public string ReferencedEntity { get; set; } = "";

    /// <summary>Gets or sets the logical name of the referencing (child / "many" side) entity.</summary>
    public string ReferencingEntity { get; set; } = "";

    /// <summary>Gets or sets the schema name for the relationship.</summary>
    public string SchemaName { get; set; } = "";

    /// <summary>Gets or sets the schema name of the lookup column created on the referencing entity.</summary>
    public string LookupSchemaName { get; set; } = "";

    /// <summary>Gets or sets the display name of the lookup column.</summary>
    public string LookupDisplayName { get; set; } = "";

    /// <summary>Gets or sets the cascade configuration for the relationship.</summary>
    public CascadeConfigurationDto? CascadeConfiguration { get; set; }

    /// <summary>Gets or sets whether this is a hierarchical relationship.</summary>
    public bool? IsHierarchical { get; set; }

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
