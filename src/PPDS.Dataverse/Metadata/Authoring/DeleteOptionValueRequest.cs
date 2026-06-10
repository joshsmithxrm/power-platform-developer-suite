namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to delete an option value from an option set.
/// Target the option by <see cref="Value"/> or <see cref="Label"/> (exactly one).
/// </summary>
public sealed class DeleteOptionValueRequest
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

    /// <summary>Gets or sets the target option label (mutually exclusive with <see cref="Value"/>) (#1169).</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets whether this is a dry-run (validate the target exists, no changes persisted) (#1172).</summary>
    public bool DryRun { get; set; }
}
