namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Defines the cascade behavior configuration for a relationship.
/// </summary>
public sealed class CascadeConfigurationDto
{
    /// <summary>Gets or sets the cascade behavior for assign operations.</summary>
    public CascadeBehavior? Assign { get; set; }

    /// <summary>Gets or sets the cascade behavior for delete operations.</summary>
    public CascadeBehavior? Delete { get; set; }

    /// <summary>Gets or sets the cascade behavior for merge operations.</summary>
    public CascadeBehavior? Merge { get; set; }

    /// <summary>Gets or sets the cascade behavior for reparent operations.</summary>
    public CascadeBehavior? Reparent { get; set; }

    /// <summary>Gets or sets the cascade behavior for share operations.</summary>
    public CascadeBehavior? Share { get; set; }

    /// <summary>Gets or sets the cascade behavior for unshare operations.</summary>
    public CascadeBehavior? Unshare { get; set; }
}
