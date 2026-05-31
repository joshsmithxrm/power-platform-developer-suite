namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to add an option to a column-scoped (local) option set (#1161).
/// </summary>
public sealed class AddColumnOptionRequest
{
    /// <summary>Gets or sets the entity logical name owning the column.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the Choice/Choices column.</summary>
    public string ColumnLogicalName { get; set; } = "";

    /// <summary>Gets or sets the label for the new option.</summary>
    public string Label { get; set; } = "";

    /// <summary>Gets or sets the explicit option value. When null, derived from <see cref="SolutionUniqueName"/>.</summary>
    public int? Value { get; set; }

    /// <summary>Gets or sets the solution unique name used for publisher-prefix value derivation when <see cref="Value"/> is null.</summary>
    public string? SolutionUniqueName { get; set; }

    /// <summary>Gets or sets the option color (hex string, e.g. #FF0000).</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets whether to publish the entity after the change.</summary>
    public bool Publish { get; set; }

    /// <summary>Gets or sets whether to validate only, without persisting changes.</summary>
    public bool DryRun { get; set; }
}
