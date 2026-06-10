namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to update an existing option value in an option set.
/// Target the option by <see cref="Value"/> or <see cref="Label"/> (exactly one);
/// <see cref="NewLabel"/> carries the updated label (#1170).
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

    /// <summary>Gets or sets the target option value (mutually exclusive with <see cref="Label"/>).</summary>
    public int? Value { get; set; }

    /// <summary>Gets or sets the target option current label (mutually exclusive with <see cref="Value"/>).</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the new label to apply (optional; the current label is preserved when omitted).</summary>
    public string? NewLabel { get; set; }

    /// <summary>Gets or sets the updated description for the option.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the updated color for the option (hex string).</summary>
    public string? Color { get; set; }
}
