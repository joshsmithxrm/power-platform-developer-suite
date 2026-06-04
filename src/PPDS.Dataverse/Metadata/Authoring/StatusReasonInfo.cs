namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>Projection of a statuscode option value for list output.</summary>
public sealed class StatusReasonInfo
{
    /// <summary>Numeric option value.</summary>
    public int Value { get; set; }

    /// <summary>Display label.</summary>
    public string Label { get; set; } = "";

    /// <summary>State code: 0 (Active) or 1 (Inactive).</summary>
    public int StateCode { get; set; }

    /// <summary>Human-readable state label ("Active" or "Inactive").</summary>
    public string StateLabel { get; set; } = "";

    /// <summary>Hex color string, or null.</summary>
    public string? Color { get; set; }
}
