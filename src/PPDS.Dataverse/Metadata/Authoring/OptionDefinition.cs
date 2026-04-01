namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Defines a single option value for a choice or choices column.
/// </summary>
public sealed class OptionDefinition
{
    /// <summary>Gets or sets the display label for the option.</summary>
    public string Label { get; set; } = "";

    /// <summary>Gets or sets the numeric value of the option.</summary>
    public int Value { get; set; }

    /// <summary>Gets or sets the description of the option.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the color associated with the option (hex string).</summary>
    public string? Color { get; set; }
}
