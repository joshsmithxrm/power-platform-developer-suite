namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to reorder the option values in an option set.
/// </summary>
public sealed class ReorderOptionsRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the option set.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the name of the option set.</summary>
    public string OptionSetName { get; set; } = "";

    /// <summary>Gets or sets the entity logical name (for local option sets).</summary>
    public string? EntityLogicalName { get; set; }

    /// <summary>Gets or sets the attribute logical name (for local option sets).</summary>
    public string? AttributeLogicalName { get; set; }

    /// <summary>Gets or sets the ordered list of option values defining the new order.</summary>
    public int[] Order { get; set; } = [];
}
