namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to update an existing global choice (option set) in Dataverse.
/// </summary>
public sealed class UpdateGlobalChoiceRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the global choice.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the name of the global choice to update.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the updated display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the updated description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
