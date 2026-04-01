namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to delete an option value from an option set.
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

    /// <summary>Gets or sets the numeric value of the option to delete.</summary>
    public int Value { get; set; }
}
