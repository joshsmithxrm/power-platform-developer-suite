namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to update an option on a column-scoped (local) option set (#1161).
/// Target the option by <see cref="Value"/> or <see cref="Label"/> (exactly one).
/// </summary>
public sealed class UpdateColumnOptionRequest
{
    /// <summary>Gets or sets the entity logical name owning the column.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the Choice/Choices column.</summary>
    public string ColumnLogicalName { get; set; } = "";

    /// <summary>Gets or sets the target option value (mutually exclusive with <see cref="Label"/>).</summary>
    public int? Value { get; set; }

    /// <summary>Gets or sets the target option current label (mutually exclusive with <see cref="Value"/>).</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the new label to apply (optional).</summary>
    public string? NewLabel { get; set; }

    /// <summary>Gets or sets the new color (hex string, optional).</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets the solution unique name.</summary>
    public string? SolutionUniqueName { get; set; }

    /// <summary>Gets or sets whether to publish the entity after the change.</summary>
    public bool Publish { get; set; }

    /// <summary>Gets or sets whether to validate only, without persisting changes.</summary>
    public bool DryRun { get; set; }
}
