namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>Request to remove a status reason from an entity.</summary>
public sealed class RemoveStatusReasonRequest
{
    /// <summary>Logical name of the entity.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Target by value (exactly one of Value/Label required).</summary>
    public int? Value { get; set; }

    /// <summary>Target by label (exactly one of Value/Label required).</summary>
    public string? Label { get; set; }

    /// <summary>Solution unique name.</summary>
    public string? SolutionUniqueName { get; set; }

    /// <summary>Publish the entity after the change.</summary>
    public bool Publish { get; set; }
}
