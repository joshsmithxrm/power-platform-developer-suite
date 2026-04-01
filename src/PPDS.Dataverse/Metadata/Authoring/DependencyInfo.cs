namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Describes a single component dependency found during a delete dry-run.
/// </summary>
public sealed class DependencyInfo
{
    /// <summary>Gets or sets the type of the dependent component (e.g., "Entity", "Attribute", "Form").</summary>
    public string DependentComponentType { get; set; } = "";

    /// <summary>Gets or sets the display name of the dependent component.</summary>
    public string DependentComponentName { get; set; } = "";

    /// <summary>Gets or sets the schema name of the dependent component, if available.</summary>
    public string? DependentComponentSchemaName { get; set; }
}
