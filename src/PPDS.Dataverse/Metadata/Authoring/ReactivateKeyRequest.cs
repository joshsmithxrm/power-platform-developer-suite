namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to reactivate a failed alternate key on a Dataverse table.
/// </summary>
public sealed class ReactivateKeyRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the table.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the entity containing the key.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the key to reactivate.</summary>
    public string KeyLogicalName { get; set; } = "";
}
