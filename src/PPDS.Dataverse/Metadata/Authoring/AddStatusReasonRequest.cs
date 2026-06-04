namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>Request to add a status reason (statuscode option value) to an entity.</summary>
public sealed class AddStatusReasonRequest
{
    /// <summary>Logical name of the entity.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Display label for the new status reason.</summary>
    public string Label { get; set; } = "";

    /// <summary>State code the reason belongs to: 0 (Active) or 1 (Inactive).</summary>
    public int StateCode { get; set; }

    /// <summary>Explicit option value; when null, derived via OptionValueDeriver.</summary>
    public int? Value { get; set; }

    /// <summary>Solution unique name — required for value derivation when Value is null.</summary>
    public string? SolutionUniqueName { get; set; }

    /// <summary>Optional hex color.</summary>
    public string? Color { get; set; }

    /// <summary>Publish the entity after the change.</summary>
    public bool Publish { get; set; }

    /// <summary>Validate without executing SDK call.</summary>
    public bool DryRun { get; set; }
}
