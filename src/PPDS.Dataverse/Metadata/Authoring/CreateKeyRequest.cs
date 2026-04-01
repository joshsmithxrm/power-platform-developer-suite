namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to create an alternate key on a Dataverse table.
/// </summary>
public sealed class CreateKeyRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the table.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the entity to add the key to.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the schema name for the key.</summary>
    public string SchemaName { get; set; } = "";

    /// <summary>Gets or sets the display name of the key.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Gets or sets the column logical names that make up the key (1-16 columns).</summary>
    public string[] KeyAttributes { get; set; } = [];

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
