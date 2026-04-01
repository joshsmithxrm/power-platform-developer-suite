namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to delete a column (attribute) from a Dataverse table.
/// </summary>
public sealed class DeleteColumnRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the table.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the entity containing the column.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the column to delete.</summary>
    public string ColumnLogicalName { get; set; } = "";

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
