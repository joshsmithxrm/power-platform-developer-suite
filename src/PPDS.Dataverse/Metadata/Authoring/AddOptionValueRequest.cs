namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to add a new option value to an existing option set.
/// </summary>
public sealed class AddOptionValueRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the option set.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the name of the option set.</summary>
    public string OptionSetName { get; set; } = "";

    /// <summary>Gets or sets the entity logical name (for local option sets).</summary>
    public string? EntityLogicalName { get; set; }

    /// <summary>Gets or sets the attribute logical name (for local option sets).</summary>
    public string? AttributeLogicalName { get; set; }

    /// <summary>Gets or sets the label for the new option.</summary>
    public string Label { get; set; } = "";

    /// <summary>Gets or sets the numeric value for the new option. Auto-assigned if null.</summary>
    public int? Value { get; set; }

    /// <summary>Gets or sets the description for the new option.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the color associated with the option (hex string).</summary>
    public string? Color { get; set; }
}
