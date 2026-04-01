namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to create a new global choice (option set) in Dataverse.
/// </summary>
public sealed class CreateGlobalChoiceRequest
{
    /// <summary>Gets or sets the unique name of the solution to add the choice to.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the schema name for the global choice.</summary>
    public string SchemaName { get; set; } = "";

    /// <summary>Gets or sets the display name of the global choice.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Gets or sets the description of the global choice.</summary>
    public string Description { get; set; } = "";

    /// <summary>Gets or sets the option definitions for the global choice.</summary>
    public OptionDefinition[] Options { get; set; } = [];

    /// <summary>Gets or sets whether this is a multi-select choice.</summary>
    public bool IsMultiSelect { get; set; }

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
