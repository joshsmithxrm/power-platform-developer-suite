namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to delete a global choice (option set) from Dataverse.
/// </summary>
public sealed class DeleteGlobalChoiceRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the global choice.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the name of the global choice to delete.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets the metadata id a preview resolved and the user confirmed. When set, the deletion is refused
    /// unless the pre-delete recheck finds exactly this object under <see cref="Name"/>.
    /// </summary>
    public System.Guid? ExpectedMetadataId { get; set; }
}
