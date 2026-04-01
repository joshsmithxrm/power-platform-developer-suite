namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to create a many-to-many (N:N) relationship between two Dataverse tables.
/// </summary>
public sealed class CreateManyToManyRequest
{
    /// <summary>Gets or sets the unique name of the solution to add the relationship to.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the first entity.</summary>
    public string Entity1LogicalName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the second entity.</summary>
    public string Entity2LogicalName { get; set; } = "";

    /// <summary>Gets or sets the schema name for the relationship.</summary>
    public string SchemaName { get; set; } = "";

    /// <summary>Gets or sets the schema name of the intersect entity.</summary>
    public string? IntersectEntitySchemaName { get; set; }

    /// <summary>Gets or sets the navigation property name on Entity1.</summary>
    public string? Entity1NavigationPropertyName { get; set; }

    /// <summary>Gets or sets the navigation property name on Entity2.</summary>
    public string? Entity2NavigationPropertyName { get; set; }

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
