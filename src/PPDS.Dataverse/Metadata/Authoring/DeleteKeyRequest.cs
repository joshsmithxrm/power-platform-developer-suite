namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to delete an alternate key from a Dataverse table.
/// </summary>
public sealed class DeleteKeyRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the table.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the entity containing the key.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the key to delete.</summary>
    public string KeyLogicalName { get; set; } = "";

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
