using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Defines a single option value for a choice or choices column.
/// </summary>
[JsonConverter(typeof(OptionDefinitionJsonConverter))]
public sealed class OptionDefinition
{
    private int _value;
    private bool _hasExplicitValue;

    /// <summary>Gets or sets the display label for the option.</summary>
    public string Label { get; set; } = "";

    /// <summary>Gets or sets the numeric value of the option. Setting zero explicitly preserves zero.</summary>
    public int Value
    {
        get => _value;
        set { _value = value; _hasExplicitValue = true; }
    }

    /// <summary>Gets whether the numeric value was explicitly supplied rather than omitted.</summary>
    [JsonIgnore]
    public bool HasExplicitValue => _hasExplicitValue;

    /// <summary>Gets or sets the description of the option.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the color associated with the option (hex string).</summary>
    public string? Color { get; set; }
}
