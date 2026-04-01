namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to update an existing option value in an option set.
/// </summary>
public sealed class UpdateOptionValueRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the option set.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the name of the option set.</summary>
    public string OptionSetName { get; set; } = "";

    /// <summary>Gets or sets the entity logical name (for local option sets).</summary>
    public string? EntityLogicalName { get; set; }

    /// <summary>Gets or sets the attribute logical name (for local option sets).</summary>
    public string? AttributeLogicalName { get; set; }

    /// <summary>Gets or sets the numeric value of the option to update.</summary>
    public int Value { get; set; }

    /// <summary>Gets or sets the updated label for the option.</summary>
    public string Label { get; set; } = "";

    /// <summary>Gets or sets the updated description for the option.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the updated color for the option (hex string).</summary>
    public string? Color { get; set; }
}
