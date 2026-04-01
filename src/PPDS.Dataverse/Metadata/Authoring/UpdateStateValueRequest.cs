namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to update a state or status option value label.
/// </summary>
public sealed class UpdateStateValueRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the entity.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the entity.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the state/status attribute.</summary>
    public string AttributeLogicalName { get; set; } = "";

    /// <summary>Gets or sets the numeric value of the state/status option to update.</summary>
    public int Value { get; set; }

    /// <summary>Gets or sets the updated label for the state/status option.</summary>
    public string Label { get; set; } = "";
}
