namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>Request to update an existing status reason on an entity.</summary>
public sealed class UpdateStatusReasonRequest
{
    /// <summary>Logical name of the entity.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Target by value (exactly one of Value/Label required).</summary>
    public int? Value { get; set; }

    /// <summary>Target by label (exactly one of Value/Label required).</summary>
    public string? Label { get; set; }

    /// <summary>New label to apply.</summary>
    public string? NewLabel { get; set; }

    /// <summary>New color to apply.</summary>
    public string? Color { get; set; }

    /// <summary>Solution unique name.</summary>
    public string? SolutionUniqueName { get; set; }

    /// <summary>Publish the entity after the change.</summary>
    public bool Publish { get; set; }
}
