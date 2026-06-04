namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Projection of a single option on a column-scoped (local) option set (#1161).
/// </summary>
public sealed class ColumnOptionInfo
{
    /// <summary>Gets or sets the option value.</summary>
    public int Value { get; set; }

    /// <summary>Gets or sets the option label.</summary>
    public string Label { get; set; } = "";

    /// <summary>Gets or sets the option color (hex string), if set.</summary>
    public string? Color { get; set; }
}
